"""Automated pytest suite for Blueipooth Bumble Bluetooth 5.2 Virtual Engine."""
from __future__ import annotations

import asyncio
import json
import sys
from pathlib import Path
from typing import Any

import pytest
from bumble import hci
from bumble.transport.common import PacketParser

ENGINE_DIR = Path(__file__).resolve().parent.parent
if str(ENGINE_DIR) not in sys.path:
    sys.path.insert(0, str(ENGINE_DIR))

from main import BlueipoothEngineRuntime  # noqa: E402


class H4TestClient:
    """Helper async HCI H4 client simulating Windows BthUsb.sys talking to our engine."""

    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        self.reader = reader
        self.writer = writer
        self.events: asyncio.Queue[hci.HCI_Packet] = asyncio.Queue()
        self._parser = PacketParser(self)
        self._pump_task = asyncio.create_task(self._read_loop())

    def on_packet(self, raw: bytes) -> None:
        pkt = hci.HCI_Packet.from_bytes(raw)
        self.events.put_nowait(pkt)

    async def _read_loop(self) -> None:
        try:
            while not self.reader.at_eof():
                data = await self.reader.read(4096)
                if not data:
                    break
                self._parser.feed_data(data)
        except asyncio.CancelledError:
            pass

    async def send_command(self, cmd: hci.HCI_Command) -> None:
        self.writer.write(bytes(cmd))
        await self.writer.drain()

    async def next_event(self, timeout: float = 2.0) -> hci.HCI_Packet:
        return await asyncio.wait_for(self.events.get(), timeout=timeout)

    async def close(self) -> None:
        self._pump_task.cancel()
        self.writer.close()


@pytest.mark.asyncio
async def test_windows_bthusb_initialization_and_ble_proxy() -> None:
    runtime = BlueipoothEngineRuntime(h4_port=0, ipc_port=0)
    h4_port, ipc_port = await runtime.start()

    try:
        # Connect IPC client (.NET Host simulator)
        ipc_reader, ipc_writer = await asyncio.open_connection("127.0.0.1", ipc_port)
        ipc_msgs: asyncio.Queue[dict[str, Any]] = asyncio.Queue()

        async def pump_ipc() -> None:
            while not ipc_reader.at_eof():
                line = await ipc_reader.readline()
                if not line:
                    break
                ipc_msgs.put_nowait(json.loads(line.decode("utf-8")))

        ipc_task = asyncio.create_task(pump_ipc())

        # Connect H4 HCI client (Windows BthUsb.sys simulator)
        h4_r, h4_w = await asyncio.open_connection("127.0.0.1", h4_port)
        h4 = H4TestClient(h4_r, h4_w)

        # 1. HCI Reset + Read BD_ADDR + Read Local Version
        await h4.send_command(hci.HCI_Reset_Command())
        evt = await h4.next_event()
        assert isinstance(evt, hci.HCI_Command_Complete_Event)

        await h4.send_command(hci.HCI_Read_BD_ADDR_Command())
        evt = await h4.next_event()
        assert isinstance(evt, hci.HCI_Command_Complete_Event)
        assert str(evt.return_parameters.bd_addr).startswith("B1:7E:00:00:00:01")

        # 2. Enable LE Scan -> Expect ble_scan_cmd forwarded to IPC!
        await h4.send_command(
            hci.HCI_LE_Set_Scan_Enable_Command(le_scan_enable=1, filter_duplicates=0)
        )
        evt = await h4.next_event()
        assert isinstance(evt, hci.HCI_Command_Complete_Event)

        scan_ipc = await asyncio.wait_for(ipc_msgs.get(), timeout=2.0)
        assert scan_ipc["type"] == "ble_scan_cmd"
        assert scan_ipc["enable"] is True

        # 3. Inject real BLE Advertisement from Android -> Expect Windows HCI LE Advertising Report!
        adv_payload = bytes([0x02, 0x01, 0x06, 0x05, 0x09]) + b"TV55"
        ipc_writer.write(
            (
                json.dumps(
                    {
                        "type": "ble_adv_report",
                        "address": "C4:12:34:56:78:9A",
                        "addrType": 1,
                        "rssi": -51,
                        "connectable": True,
                        "dataHex": adv_payload.hex(),
                    }
                )
                + "\n"
            ).encode("utf-8")
        )
        await ipc_writer.drain()

        adv_evt = await h4.next_event()
        assert isinstance(
            adv_evt,
            (
                hci.HCI_LE_Advertising_Report_Event,
                hci.HCI_LE_Extended_Advertising_Report_Event,
            ),
        )
        assert len(adv_evt.reports) == 1
        assert adv_evt.reports[0].data == adv_payload

        # 4. Enable BLE Advertising on Windows -> Expect ble_adv_cmd on IPC!
        win_adv_data = bytes([0x02, 0x01, 0x06, 0x0B, 0x09]) + b"Blueipooth"
        await h4.send_command(
            hci.HCI_LE_Set_Advertising_Data_Command(advertising_data=win_adv_data)
        )
        await h4.next_event()
        await h4.send_command(
            hci.HCI_LE_Set_Advertising_Enable_Command(advertising_enable=1)
        )
        await h4.next_event()

        adv_cmd_ipc = await asyncio.wait_for(ipc_msgs.get(), timeout=2.0)
        assert adv_cmd_ipc["type"] == "ble_adv_cmd"
        assert adv_cmd_ipc["enable"] is True
        assert adv_cmd_ipc["advDataHex"] == win_adv_data.hex()

        await h4.close()
        ipc_task.cancel()
        ipc_writer.close()
    finally:
        await runtime.stop()


@pytest.mark.asyncio
async def test_dual_mac_headset_synthesis_and_classic_inquiry() -> None:
    runtime = BlueipoothEngineRuntime(h4_port=0, ipc_port=0)
    h4_port, ipc_port = await runtime.start()

    try:
        ipc_reader, ipc_writer = await asyncio.open_connection("127.0.0.1", ipc_port)
        h4_r, h4_w = await asyncio.open_connection("127.0.0.1", h4_port)
        h4 = H4TestClient(h4_r, h4_w)

        await h4.send_command(hci.HCI_Reset_Command())
        await h4.next_event()

        # Notify engine that Android connected to "AirPods Pro"
        ipc_writer.write(
            (
                json.dumps(
                    {
                        "type": "headset_state",
                        "connected": True,
                        "name": "AirPods Pro",
                        "mac": "AA:11:22:33:44:55",
                        "hasMic": True,
                    }
                )
                + "\n"
            ).encode("utf-8")
        )
        await ipc_writer.drain()

        synth_line = await asyncio.wait_for(ipc_reader.readline(), timeout=3.0)
        synth_msg = json.loads(synth_line.decode("utf-8"))
        assert synth_msg["type"] == "dual_mac_synthesized"
        assert synth_msg["stereoMac"] != synth_msg["micMac"]

        # Trigger Classic Bluetooth Inquiry from Windows!
        await h4.send_command(
            hci.HCI_Inquiry_Command(lap=0x9E8B33, inquiry_length=4, num_responses=0)
        )

        discovered_macs: set[str] = set()
        inquiry_completed = False
        for _ in range(6):
            evt = await h4.next_event(timeout=3.0)
            if isinstance(evt, hci.HCI_Extended_Inquiry_Result_Event):
                addr_clean = str(evt.bd_addr).split("/")[0]
                discovered_macs.add(addr_clean)
                assert len(evt.extended_inquiry_response) == 240
            elif isinstance(evt, hci.HCI_Inquiry_Complete_Event):
                inquiry_completed = True
                break

        assert inquiry_completed
        assert synth_msg["stereoMac"] in discovered_macs
        assert synth_msg["micMac"] in discovered_macs

        await h4.close()
        ipc_writer.close()
    finally:
        await runtime.stop()
