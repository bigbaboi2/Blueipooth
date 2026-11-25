"""Entry point for the Blueipooth Bumble Bluetooth 5.2 Virtual HCI Engine."""
from __future__ import annotations

import argparse
import asyncio
import logging
from pathlib import Path
import signal
import sys
from typing import Any

_ENGINE_DIR = str(Path(__file__).resolve().parent)
if _ENGINE_DIR not in sys.path:
    sys.path.insert(0, _ENGINE_DIR)

from bumble import link
from bumble.transport.common import PacketParser

from ipc_protocol import ControlIpcServer
from virtual_controller import BlueipoothVirtualController
from virtual_headset import DualMacVirtualHeadsetManager

logger = logging.getLogger("blueipooth.bumble")


class H4TcpServer:
    """
    Local TCP server (127.0.0.1:49552) speaking standard Bluetooth HCI H4 framing
    to the .NET Blueipooth.UsbIp server. Supports seamless reconnects across USB/IP cycles.
    """

    def __init__(
        self,
        controller: BlueipoothVirtualController,
        host: str = "127.0.0.1",
        port: int = 49552,
    ) -> None:
        self.controller = controller
        self.host = host
        self.port = port
        self._server: asyncio.AbstractServer | None = None
        self._active_writer: asyncio.StreamWriter | None = None
        self.controller.host = self

    def on_packet(self, packet: bytes) -> None:
        """Called by Bumble Controller whenever an HCI Event / ACL / SCO packet is sent to Windows."""
        if self._active_writer is not None and not self._active_writer.is_closing():
            try:
                self._active_writer.write(packet)
            except Exception as exc:
                logger.debug("Failed writing H4 packet: %s", exc)

    async def start(self) -> int:
        self._server = await asyncio.start_server(
            self._handle_client, self.host, self.port
        )
        sockets = self._server.sockets
        if sockets:
            self.port = sockets[0].getsockname()[1]
        logger.info("H4 HCI TCP server listening on %s:%d", self.host, self.port)
        return self.port

    async def stop(self) -> None:
        if self._active_writer is not None:
            self._active_writer.close()
            self._active_writer = None
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()
            self._server = None

    async def _handle_client(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter
    ) -> None:
        if self._active_writer is not None:
            try:
                self._active_writer.close()
            except Exception:
                pass
        self._active_writer = writer
        peer = writer.get_extra_info("peername")
        logger.info("USB/IP HCI Host bridge connected: %s", peer)

        class _ParserSink:
            def __init__(self, ctrl: BlueipoothVirtualController) -> None:
                self._ctrl = ctrl

            def on_packet(self, packet: bytes) -> None:
                self._ctrl.on_packet(packet)

        parser = PacketParser(_ParserSink(self.controller))
        try:
            while not reader.at_eof():
                chunk = await reader.read(4096)
                if not chunk:
                    break
                parser.feed_data(chunk)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            logger.info("USB/IP HCI Host bridge disconnected: %s", exc)
        finally:
            if self._active_writer is writer:
                self._active_writer = None
            writer.close()


class BlueipoothEngineRuntime:
    """Coordinates VirtualLink, BlueipoothVirtualController, DualMacVirtualHeadsetManager, H4 & IPC."""

    def __init__(
        self,
        h4_host: str = "127.0.0.1",
        h4_port: int = 49552,
        ipc_port: int = 49553,
        bd_addr: str = "B1:7E:00:00:00:01",
    ) -> None:
        self.virtual_link = link.LocalLink()
        self.ipc_server = ControlIpcServer(
            host=h4_host, port=ipc_port, on_message=self._on_ipc_message
        )
        self.controller = BlueipoothVirtualController(
            name="Blueipooth 5.2 Adapter",
            virtual_link=self.virtual_link,
            public_address=bd_addr,
            ipc_emit=self.ipc_server.send_event,
        )
        self.headset_manager = DualMacVirtualHeadsetManager(
            virtual_link=self.virtual_link,
            main_controller=self.controller,
            ipc_emit=self.ipc_server.send_event,
        )
        self.h4_server = H4TcpServer(
            controller=self.controller, host=h4_host, port=h4_port
        )

    async def start(self) -> tuple[int, int]:
        ipc_port = await self.ipc_server.start()
        h4_port = await self.h4_server.start()
        await self.ipc_server.send_event(
            {
                "type": "engine_ready",
                "bdAddr": str(self.controller.public_address),
                "h4Port": h4_port,
                "ipcPort": ipc_port,
            }
        )
        return h4_port, ipc_port

    async def stop(self) -> None:
        await self.headset_manager.detach_headset()
        await self.h4_server.stop()
        await self.ipc_server.stop()

    async def _on_ipc_message(self, msg: dict[str, Any]) -> None:
        msg_type = msg.get("type")
        if msg_type == "ble_adv_report":
            self.controller.inject_remote_ble_advertisement(
                address_str=str(msg.get("address", "00:00:00:00:00:00")),
                addr_type=int(msg.get("addrType", 1)),
                rssi=int(msg.get("rssi", -60)),
                connectable=bool(msg.get("connectable", True)),
                data=bytes.fromhex(msg.get("dataHex", "")),
            )
        elif msg_type == "headset_state":
            if msg.get("connected"):
                stereo_mac, mic_mac = await self.headset_manager.attach_headset(
                    name=str(msg.get("name", "Bluetooth Headset")),
                    remote_mac=str(msg.get("mac", "")),
                    has_mic=bool(msg.get("hasMic", True)),
                )
                await self.ipc_server.send_event(
                    {
                        "type": "dual_mac_synthesized",
                        "stereoMac": stereo_mac,
                        "micMac": mic_mac,
                    }
                )
            else:
                await self.headset_manager.detach_headset()
                await self.ipc_server.send_event({"type": "dual_mac_detached"})
        elif msg_type == "audio_mode":
            self.headset_manager.audio_mode = str(msg.get("mode", "GamingOpus"))
        elif msg_type == "ping":
            await self.ipc_server.send_event(
                {
                    "type": "pong",
                    "bdAddr": str(self.controller.public_address),
                }
            )


async def _async_main(args: argparse.Namespace) -> None:
    runtime = BlueipoothEngineRuntime(
        h4_host=args.host,
        h4_port=args.h4_port,
        ipc_port=args.ipc_port,
        bd_addr=args.bd_addr,
    )
    h4_port, ipc_port = await runtime.start()
    print(f"BLUEIPOOTH_BUMBLE_READY H4={h4_port} IPC={ipc_port}", flush=True)

    stop_event = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            loop.add_signal_handler(sig, stop_event.set)
        except NotImplementedError:
            # Windows ProactorEventLoop does not support add_signal_handler
            pass

    try:
        await stop_event.wait()
    finally:
        await runtime.stop()


def main() -> None:
    parser = argparse.ArgumentParser(description="Blueipooth Bumble HCI 5.2 Engine")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--h4-port", type=int, default=49552)
    parser.add_argument("--ipc-port", type=int, default=49553)
    parser.add_argument("--bd-addr", default="B1:7E:00:00:00:01")
    parser.add_argument("--log-level", default="INFO")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        handlers=[
            logging.StreamHandler(sys.stdout),
            logging.FileHandler(str(Path(_ENGINE_DIR).parent.parent / "hci_debug.log"), mode="w", encoding="utf-8"),
        ],
    )
    try:
        asyncio.run(_async_main(args))
    except KeyboardInterrupt:
        sys.exit(0)


if __name__ == "__main__":
    main()
