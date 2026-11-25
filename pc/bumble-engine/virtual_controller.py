"""Custom Bumble Dual-Mode Bluetooth 5.2 Virtual Controller with Android BLE RF Proxy & Classic Inquiry."""
from __future__ import annotations

import asyncio
import dataclasses
import logging
import struct
from typing import Any, Awaitable, Callable

from bumble import controller, hci, link

logger = logging.getLogger(__name__)


@dataclasses.dataclass
class VirtualClassicPeerInfo:
    """Metadata advertised during Classic BR/EDR Inquiry for synthesized peers (e.g., Dual-MAC Headsets)."""

    address: hci.Address
    name: str
    class_of_device: int
    service_uuids_16: list[int] = dataclasses.field(default_factory=list)
    rssi: int = -42

    def build_eir_240(self) -> bytes:
        """Build a spec-compliant 240-byte Extended Inquiry Response (EIR) payload."""
        chunks = bytearray()
        name_bytes = self.name.encode("utf-8")[:120]
        if name_bytes:
            # 0x09 = Complete Local Name
            chunks.append(len(name_bytes) + 1)
            chunks.append(0x09)
            chunks.extend(name_bytes)

        if self.service_uuids_16:
            # 0x03 = Complete List of 16-bit Service Class UUIDs
            uuid_payload = b"".join(struct.pack("<H", u) for u in self.service_uuids_16)
            chunks.append(len(uuid_payload) + 1)
            chunks.append(0x03)
            chunks.extend(uuid_payload)

        return bytes(chunks).ljust(240, b"\x00")[:240]


class BlueipoothVirtualController(controller.Controller):
    """
    Dual-Mode (BR/EDR + BLE 5.2) Virtual HCI Controller for Windows 10/11 BthUsb.sys.
    - Delegates BLE Scan / BLE Advertise / Remote GATT to Android Companion over IPC.
    - Synthesizes Classic BR/EDR Inquiry responses for Dual-MAC Virtual Headsets.
    - Gracefully handles Windows-specific HCI configuration opcodes.
    """

    def __init__(
        self,
        name: str = "Blueipooth Virtual Radio",
        host_source: Any = None,
        host_sink: Any = None,
        virtual_link: link.LocalLink | None = None,
        public_address: str | bytes | hci.Address = "B1:7E:00:00:00:01",
        ipc_emit: Callable[[dict[str, Any]], Awaitable[None]] | None = None,
    ) -> None:
        super().__init__(
            name=name,
            host_source=host_source,
            host_sink=host_sink,
            link=virtual_link,
            public_address=public_address,
        )
        self.ipc_emit = ipc_emit
        self.hci_version = hci.HCI_VERSION_BLUETOOTH_CORE_5_2
        self.lmp_version = hci.HCI_VERSION_BLUETOOTH_CORE_5_2
        self.acl_data_packet_length = 1021
        self.total_num_acl_data_packets = 64
        self.le_acl_data_packet_length = 251
        self.total_num_le_acl_data_packets = 64

        # Enable Dual-Mode (BR/EDR + LE), Simple Pairing, EIR, eSCO, 3/5-slot ACL
        self.lmp_features = (
            hci.LmpFeatureMask.LMP_3_SLOT_PACKETS
            | hci.LmpFeatureMask.LMP_5_SLOT_PACKETS
            | hci.LmpFeatureMask.ENCRYPTION
            | hci.LmpFeatureMask.ROLE_SWITCH
            | hci.LmpFeatureMask.SNIFF_MODE
            | hci.LmpFeatureMask.SCO_LINK
            | hci.LmpFeatureMask.CVSD_SYNCHRONOUS_DATA
            | hci.LmpFeatureMask.TRANSPARENT_SYNCHRONOUS_DATA
            | hci.LmpFeatureMask.ENHANCED_DATA_RATE_ACL_2_MBPS_MODE
            | hci.LmpFeatureMask.ENHANCED_DATA_RATE_ACL_3_MBPS_MODE
            | hci.LmpFeatureMask.RSSI_WITH_INQUIRY_RESULTS
            | hci.LmpFeatureMask.EXTENDED_SCO_LINK_EV3_PACKETS
            | hci.LmpFeatureMask.SECURE_SIMPLE_PAIRING_CONTROLLER_SUPPORT
            | hci.LmpFeatureMask.ENCAPSULATED_PDU
            | hci.LmpFeatureMask.EXTENDED_INQUIRY_RESPONSE
            | hci.LmpFeatureMask.LE_SUPPORTED_CONTROLLER
            | hci.LmpFeatureMask.SIMULTANEOUS_LE_AND_BR_EDR_TO_SAME_DEVICE_CAPABLE_CONTROLLER
            | hci.LmpFeatureMask.EXTENDED_FEATURES
        )

        # Register extra Classic HCI commands queried by Windows BthUsb.sys
        extra_cmds = [
            getattr(hci, "HCI_INQUIRY_COMMAND", None),
            getattr(hci, "HCI_INQUIRY_CANCEL_COMMAND", None),
            getattr(hci, "HCI_CHANGE_CONNECTION_PACKET_TYPE_COMMAND", None),
            getattr(hci, "HCI_SET_CONNECTION_ENCRYPTION_COMMAND", None),
            getattr(hci, "HCI_READ_REMOTE_VERSION_INFORMATION_COMMAND", None),
            getattr(hci, "HCI_READ_CLOCK_OFFSET_COMMAND", None),
            getattr(hci, "HCI_WRITE_INQUIRY_MODE_COMMAND", None),
            getattr(hci, "HCI_READ_INQUIRY_MODE_COMMAND", None),
            getattr(hci, "HCI_WRITE_PAGE_SCAN_TYPE_COMMAND", None),
            getattr(hci, "HCI_WRITE_INQUIRY_SCAN_TYPE_COMMAND", None),
            getattr(hci, "HCI_WRITE_PAGE_TIMEOUT_COMMAND", None),
            getattr(hci, "HCI_WRITE_CONNECTION_ACCEPT_TIMEOUT_COMMAND", None),
            getattr(hci, "HCI_WRITE_DEFAULT_LINK_POLICY_SETTINGS_COMMAND", None),
            getattr(hci, "HCI_READ_LINK_POLICY_SETTINGS_COMMAND", None),
            getattr(hci, "HCI_WRITE_LINK_POLICY_SETTINGS_COMMAND", None),
            getattr(hci, "HCI_ROLE_DISCOVERY_COMMAND", None),
            getattr(hci, "HCI_SNIFF_SUBRATING_COMMAND", None),
            getattr(hci, "HCI_DELETE_STORED_LINK_KEY_COMMAND", None),
            getattr(hci, "HCI_SET_EVENT_FILTER_COMMAND", None),
            getattr(hci, "HCI_FLUSH_COMMAND", None),
            getattr(hci, "HCI_READ_AUTOMATIC_FLUSH_TIMEOUT_COMMAND", None),
            getattr(hci, "HCI_WRITE_AUTOMATIC_FLUSH_TIMEOUT_COMMAND", None),
            getattr(hci, "HCI_READ_TRANSMIT_POWER_LEVEL_COMMAND", None),
            getattr(hci, "HCI_READ_LINK_SUPERVISION_TIMEOUT_COMMAND", None),
            getattr(hci, "HCI_WRITE_LINK_SUPERVISION_TIMEOUT_COMMAND", None),
            getattr(hci, "HCI_READ_INQUIRY_RESPONSE_TRANSMIT_POWER_LEVEL_COMMAND", None),
            getattr(hci, "HCI_READ_LINK_QUALITY_COMMAND", None),
            getattr(hci, "HCI_READ_RSSI_COMMAND", None),
            getattr(hci, "HCI_READ_AFH_CHANNEL_MAP_COMMAND", None),
            getattr(hci, "HCI_READ_ENCRYPTION_KEY_SIZE_COMMAND", None),
        ]
        self.supported_commands = set(self.supported_commands)
        for cmd_code in extra_cmds:
            if cmd_code is not None:
                self.supported_commands.add(cmd_code)

        self.classic_peers: dict[str, VirtualClassicPeerInfo] = {}
        self.inquiry_mode: int = 2  # 2 = Extended Inquiry Result
        self._inquiry_task: asyncio.Task[None] | None = None
        self._last_adv_data: bytes = b""
        self._last_scan_rsp_data: bytes = b""
        self._use_extended_scan_reports: bool = False

    def register_classic_peer(self, peer: VirtualClassicPeerInfo) -> None:
        self.classic_peers[str(peer.address)] = peer

    def unregister_classic_peer(self, address: str | hci.Address) -> None:
        self.classic_peers.pop(str(address), None)

    def _schedule_ipc(self, payload: dict[str, Any]) -> None:
        if self.ipc_emit is None:
            return
        try:
            loop = asyncio.get_running_loop()
            loop.create_task(self.ipc_emit(payload))
        except RuntimeError:
            pass

    # ------------------------------------------------------------------
    # BLE Scan Interception -> Forward to Android BluetoothLeScanner
    # ------------------------------------------------------------------
    def on_hci_le_set_scan_enable_command(
        self, command: hci.HCI_LE_Set_Scan_Enable_Command
    ) -> bytes:
        logger.info("Windows HCI_LE_Set_Scan_Enable: enable=%s", bool(command.le_scan_enable))
        self._use_extended_scan_reports = False
        res = super().on_hci_le_set_scan_enable_command(command)
        self._schedule_ipc(
            {
                "type": "ble_scan_cmd",
                "enable": bool(command.le_scan_enable),
                "filterDuplicates": bool(command.filter_duplicates),
            }
        )
        return res

    def on_hci_le_set_extended_scan_enable_command(
        self, command: hci.HCI_LE_Set_Extended_Scan_Enable_Command
    ) -> bytes:
        logger.info("Windows HCI_LE_Set_Extended_Scan_Enable: enable=%s", bool(command.enable))
        self._use_extended_scan_reports = True
        res = super().on_hci_le_set_extended_scan_enable_command(command)
        self._schedule_ipc(
            {
                "type": "ble_scan_cmd",
                "enable": bool(command.enable),
                "filterDuplicates": bool(command.filter_duplicates),
            }
        )
        return res

    def inject_remote_ble_advertisement(
        self,
        address_str: str,
        addr_type: int,
        rssi: int,
        connectable: bool,
        data: bytes,
    ) -> None:
        """Inject a real 2.4GHz BLE advertisement captured by the Android phone into Windows HCI."""
        if not self.le_scan_enable:
            return

        if "/" not in address_str and addr_type == 1:
            addr = hci.Address(address_str, hci.Address.RANDOM_DEVICE_ADDRESS)
        else:
            addr = hci.Address(address_str, hci.Address.PUBLIC_DEVICE_ADDRESS)

        if self._use_extended_scan_reports and (
            self.le_features & hci.LeFeatureMask.LE_EXTENDED_ADVERTISING
        ):
            evt_type = (
                hci.HCI_LE_Extended_Advertising_Report_Event.EventType.CONNECTABLE_ADVERTISING
                | hci.HCI_LE_Extended_Advertising_Report_Event.EventType.SCANNABLE_ADVERTISING
                | hci.HCI_LE_Extended_Advertising_Report_Event.EventType.LEGACY_ADVERTISING_PDU_USED
                if connectable
                else hci.HCI_LE_Extended_Advertising_Report_Event.EventType.LEGACY_ADVERTISING_PDU_USED
            )
            ext_report = hci.HCI_LE_Extended_Advertising_Report_Event.Report(
                event_type=evt_type,
                address_type=addr.address_type,
                address=addr,
                primary_phy=hci.Phy.LE_1M,
                secondary_phy=0,
                advertising_sid=0xFF,
                tx_power=127,
                rssi=max(-127, min(20, rssi)),
                periodic_advertising_interval=0,
                direct_address_type=0,
                direct_address=hci.Address.ANY,
                data=data[:229],
            )
            self.send_hci_packet(
                hci.HCI_LE_Extended_Advertising_Report_Event([ext_report])
            )
        else:
            legacy_type = 0x00 if connectable else 0x03
            report = hci.HCI_LE_Advertising_Report_Event.Report(
                event_type=legacy_type,
                address_type=addr.address_type,
                address=addr,
                data=data[:31],
                rssi=max(-127, min(20, rssi)),
            )
            self.send_hci_packet(hci.HCI_LE_Advertising_Report_Event([report]))

    # ------------------------------------------------------------------
    # BLE Advertising Interception -> Forward to Android BluetoothLeAdvertiser
    # ------------------------------------------------------------------
    def on_hci_le_set_advertising_data_command(
        self, command: hci.HCI_LE_Set_Advertising_Data_Command
    ) -> bytes:
        self._last_adv_data = bytes(command.advertising_data)
        return super().on_hci_le_set_advertising_data_command(command)

    def on_hci_le_set_scan_response_data_command(
        self, command: hci.HCI_LE_Set_Scan_Response_Data_Command
    ) -> bytes:
        self._last_scan_rsp_data = bytes(command.scan_response_data)
        return super().on_hci_le_set_scan_response_data_command(command)

    def on_hci_le_set_advertising_enable_command(
        self, command: hci.HCI_LE_Set_Advertising_Enable_Command
    ) -> bytes:
        res = super().on_hci_le_set_advertising_enable_command(command)
        self._schedule_ipc(
            {
                "type": "ble_adv_cmd",
                "enable": bool(command.advertising_enable),
                "advDataHex": self._last_adv_data.hex(),
                "scanRspHex": self._last_scan_rsp_data.hex(),
            }
        )
        return res

    def on_hci_le_set_extended_advertising_data_command(
        self, command: hci.HCI_LE_Set_Extended_Advertising_Data_Command
    ) -> bytes:
        res = super().on_hci_le_set_extended_advertising_data_command(command)
        if adv_set := self.advertising_sets.get(command.advertising_handle):
            self._last_adv_data = bytes(adv_set.data)
        return res

    def on_hci_le_set_extended_advertising_enable_command(
        self, command: hci.HCI_LE_Set_Extended_Advertising_Enable_Command
    ) -> bytes:
        res = super().on_hci_le_set_extended_advertising_enable_command(command)
        adv_hex = self._last_adv_data.hex()
        if command.advertising_handles:
            h = command.advertising_handles[0]
            if adv_set := self.advertising_sets.get(h):
                adv_hex = bytes(adv_set.data).hex()
        self._schedule_ipc(
            {
                "type": "ble_adv_cmd",
                "enable": bool(command.enable),
                "advDataHex": adv_hex,
                "scanRspHex": self._last_scan_rsp_data.hex(),
            }
        )
        return res

    # ------------------------------------------------------------------
    # Classic BR/EDR Inquiry -> Discover Dual-MAC Virtual Headsets
    # ------------------------------------------------------------------
    def on_hci_inquiry_command(self, command: hci.HCI_Inquiry_Command) -> None:
        logger.info("Windows HCI_Inquiry: length=%s, classic_peers=%d", command.inquiry_length, len(self.classic_peers))
        self._send_hci_command_status(hci.HCI_COMMAND_STATUS_PENDING, command.op_code)
        if self._inquiry_task and not self._inquiry_task.done():
            self._inquiry_task.cancel()
        self._inquiry_task = asyncio.get_running_loop().create_task(
            self._run_classic_inquiry(command.inquiry_length)
        )

    async def _run_classic_inquiry(self, inquiry_length: int) -> None:
        try:
            await asyncio.sleep(0.05)
            for peer in list(self.classic_peers.values()):
                self.send_hci_packet(
                    hci.HCI_Extended_Inquiry_Result_Event(
                        num_responses=1,
                        bd_addr=peer.address,
                        page_scan_repetition_mode=1,
                        reserved=0,
                        class_of_device=peer.class_of_device,
                        clock_offset=0,
                        rssi=peer.rssi,
                        extended_inquiry_response=peer.build_eir_240(),
                    )
                )
                await asyncio.sleep(0.02)
            # Complete promptly once all virtual peers are reported
            await asyncio.sleep(min(0.25, max(0.05, inquiry_length * 0.02)))
            self.send_hci_packet(
                hci.HCI_Inquiry_Complete_Event(status=hci.HCI_ErrorCode.SUCCESS)
            )
        except asyncio.CancelledError:
            pass

    def on_hci_inquiry_cancel_command(
        self, _command: hci.HCI_Inquiry_Cancel_Command
    ) -> hci.HCI_StatusReturnParameters:
        if self._inquiry_task and not self._inquiry_task.done():
            self._inquiry_task.cancel()
        return hci.HCI_StatusReturnParameters(hci.HCI_ErrorCode.SUCCESS)

    def on_hci_write_inquiry_mode_command(
        self, command: Any
    ) -> hci.HCI_StatusReturnParameters:
        self.inquiry_mode = getattr(command, "inquiry_mode", 2)
        return hci.HCI_StatusReturnParameters(hci.HCI_ErrorCode.SUCCESS)

    def on_hci_read_remote_version_information_command(
        self, command: hci.HCI_Read_Remote_Version_Information_Command
    ) -> None:
        self._send_hci_command_status(hci.HCI_COMMAND_STATUS_PENDING, command.op_code)
        self.send_hci_packet(
            hci.HCI_Read_Remote_Version_Information_Complete_Event(
                status=hci.HCI_ErrorCode.SUCCESS,
                connection_handle=command.connection_handle,
                version=self.lmp_version,
                manufacturer_name=0x000A,
                subversion=0x0889,
            )
        )

    def on_hci_set_connection_encryption_command(
        self, command: hci.HCI_Set_Connection_Encryption_Command
    ) -> None:
        self._send_hci_command_status(hci.HCI_COMMAND_STATUS_PENDING, command.op_code)
        enc = int(bool(command.encryption_enable))
        self.send_hci_packet(
            hci.HCI_Encryption_Change_Event(
                status=hci.HCI_ErrorCode.SUCCESS,
                connection_handle=command.connection_handle,
                encryption_enabled=enc,
            )
        )
        # Also notify peer virtual controller on LocalLink so its Host marks connection encrypted
        conn = self.find_classic_connection_by_handle(command.connection_handle)
        if conn is not None and self.link is not None:
            peer_ctrl = self.link.find_classic_controller(conn.peer_address)
            if peer_ctrl is not None:
                peer_conn = peer_ctrl.classic_connections.get(self.public_address)
                if peer_conn is not None:
                    peer_ctrl.send_hci_packet(
                        hci.HCI_Encryption_Change_Event(
                            status=hci.HCI_ErrorCode.SUCCESS,
                            connection_handle=peer_conn.handle,
                            encryption_enabled=enc,
                        )
                    )

    def on_hci_read_encryption_key_size_command(
        self, command: hci.HCI_Read_Encryption_Key_Size_Command
    ) -> hci.HCI_Read_Encryption_Key_Size_ReturnParameters:
        return hci.HCI_Read_Encryption_Key_Size_ReturnParameters(
            status=hci.HCI_ErrorCode.SUCCESS,
            connection_handle=command.connection_handle,
            key_size=16,
        )

    def on_hci_change_connection_packet_type_command(
        self, command: Any
    ) -> None:
        params = getattr(command, "parameters", b"")
        handle = getattr(command, "connection_handle", None)
        pkt_type = getattr(command, "packet_type", None)
        if handle is None:
            handle = struct.unpack("<H", params[:2])[0] if len(params) >= 2 else 1
        if pkt_type is None:
            pkt_type = struct.unpack("<H", params[2:4])[0] if len(params) >= 4 else 0xCC18
        self._send_hci_command_status(hci.HCI_COMMAND_STATUS_PENDING, command.op_code)
        self.send_hci_packet(
            hci.HCI_Connection_Packet_Type_Changed_Event(
                status=hci.HCI_ErrorCode.SUCCESS,
                connection_handle=handle,
                packet_type=pkt_type,
            )
        )

    def on_hci_read_clock_offset_command(
        self, command: Any
    ) -> None:
        params = getattr(command, "parameters", b"")
        handle = getattr(command, "connection_handle", None)
        if handle is None:
            handle = struct.unpack("<H", params[:2])[0] if len(params) >= 2 else 1
        self._send_hci_command_status(hci.HCI_COMMAND_STATUS_PENDING, command.op_code)
        self.send_hci_packet(
            hci.HCI_Read_Clock_Offset_Complete_Event(
                status=hci.HCI_ErrorCode.SUCCESS,
                connection_handle=handle,
                clock_offset=0,
            )
        )

    def _reply_command_complete(
        self, command: hci.HCI_Command, ret_obj: hci.HCI_ReturnParameters | bytes
    ) -> hci.HCI_ReturnParameters | None:
        wrapped = (
            hci.HCI_GenericReturnParameters(ret_obj)
            if isinstance(ret_obj, (bytes, bytearray))
            else ret_obj
        )
        if isinstance(command, hci.HCI_SyncCommand):
            return wrapped
        self.send_hci_packet(
            hci.HCI_Command_Complete_Event(
                num_hci_command_packets=1,
                command_opcode=command.op_code,
                return_parameters=wrapped,
            )
        )
        return None

    # ------------------------------------------------------------------
    # Benign Windows BthUsb.sys Configuration Fallback
    # ------------------------------------------------------------------
    def on_hci_command(
        self, command: hci.HCI_Command
    ) -> hci.HCI_ReturnParameters | None:
        ogf = command.op_code >> 10
        ocf = command.op_code & 0x03FF
        params = getattr(command, "parameters", b"")
        handle = struct.unpack("<H", params[:2])[0] if len(params) >= 2 else 1

        # OGF 0x01: Link Control async commands not registered as named classes in Bumble
        if ogf == 0x01 and ocf == 0x000F:  # HCI_Change_Connection_Packet_Type
            self.on_hci_change_connection_packet_type_command(command)
            return None
        if ogf == 0x01 and ocf == 0x001F:  # HCI_Read_Clock_Offset
            self.on_hci_read_clock_offset_command(command)
            return None
        if ogf == 0x01 and ocf == 0x0008:  # HCI_Create_Connection_Cancel
            addr_bytes = params[:6] if len(params) >= 6 else b"\x00" * 6
            return self._reply_command_complete(command, b"\x00" + addr_bytes)

        # OGF 0x02: Link Policy
        if ogf == 0x02:
            if ocf == 0x0007:  # HCI_QoS_Setup (async -> Command_Status + Event 0x0D)
                self._send_hci_command_status(hci.HCI_COMMAND_STATUS_PENDING, command.op_code)
                qos_payload = (params[:20] if len(params) >= 20 else params.ljust(20, b"\x00"))
                raw_evt = bytes([0x04, 0x0D, 1 + len(qos_payload), 0x00]) + qos_payload
                if self.host is not None:
                    self.host.on_packet(raw_evt)
                return None
            if ocf == 0x0009:  # HCI_Role_Discovery -> Status(0), Handle(2B), Role(0=Central)
                return self._reply_command_complete(command, struct.pack("<BHB", 0, handle, 0))
            if ocf == 0x000C:  # HCI_Read_Link_Policy_Settings -> Status(0), Handle(2B), Settings(0x0007)
                return self._reply_command_complete(command, struct.pack("<BHH", 0, handle, 0x0007))
            if ocf in {0x000D, 0x0011}:  # Write_Link_Policy_Settings, Sniff_Subrating -> Status(0), Handle(2B)
                return self._reply_command_complete(command, struct.pack("<BH", 0, handle))
            if ocf == 0x000E:  # Read_Default_Link_Policy_Settings
                return self._reply_command_complete(command, struct.pack("<BH", 0, 0x0007))
            if ocf == 0x000F:  # Write_Default_Link_Policy_Settings
                return self._reply_command_complete(command, hci.HCI_StatusReturnParameters(hci.HCI_ErrorCode.SUCCESS))

        # OGF 0x03: Controller & Baseband
        if ogf == 0x03:
            if ocf == 0x0012:  # Delete_Stored_Link_Key -> Status(0), NumKeysDeleted(2B=0)
                return self._reply_command_complete(command, struct.pack("<BH", 0, 0))
            if ocf in {0x0008, 0x0028, 0x0037}:  # Flush, Write_Automatic_Flush_Timeout, Write_Link_Supervision_Timeout -> Status(0), Handle(2B)
                return self._reply_command_complete(command, struct.pack("<BH", 0, handle))
            if ocf == 0x0027:  # Read_Automatic_Flush_Timeout -> Status(0), Handle(2B), Timeout(0)
                return self._reply_command_complete(command, struct.pack("<BHH", 0, handle, 0))
            if ocf == 0x0036:  # Read_Link_Supervision_Timeout -> Status(0), Handle(2B), Timeout(0x7D00)
                return self._reply_command_complete(command, struct.pack("<BHH", 0, handle, 0x7D00))
            if ocf == 0x002D:  # Read_Transmit_Power_Level -> Status(0), Handle(2B), TxPower(4 dBm)
                return self._reply_command_complete(command, struct.pack("<BHb", 0, handle, 4))
            if ocf == 0x0058:  # Read_Inquiry_Response_Transmit_Power_Level -> Status(0), TxPower(4 dBm)
                return self._reply_command_complete(command, struct.pack("<Bb", 0, 4))
            if ocf in {
                0x0005,  # Set Event Filter
                0x0016,  # Write Connection Accept Timeout
                0x0018,  # Write Page Timeout
                0x001A,  # Write Scan Enable
                0x001C,  # Write Page Scan Activity
                0x001E,  # Write Inquiry Scan Activity
                0x0020,  # Write Authentication Enable
                0x0024,  # Write Class of Device
                0x0026,  # Write Voice Setting
                0x0033,  # Host Buffer Size
                0x003F,  # Set AFH Host Channel Classification
                0x0043,  # Write Inquiry Scan Type
                0x0045,  # Write Inquiry Mode
                0x0047,  # Write Page Scan Type
                0x005B,  # Write Default Erroneous Data Reporting
                0x006D,  # Write LE Host Support
                0x0079,  # Read Secure Connections Host Support
                0x007A,  # Write Secure Connections Host Support
            }:
                if ocf == 0x0079:
                    return self._reply_command_complete(command, struct.pack("<BB", 0, 1))
                return self._reply_command_complete(command, hci.HCI_StatusReturnParameters(hci.HCI_ErrorCode.SUCCESS))

        # OGF 0x05: Status Parameters (all sync -> Command_Complete)
        if ogf == 0x05:
            if ocf == 0x0001:  # Read_Failed_Contact_Counter -> Status(0), Handle(2B), Counter(0)
                return self._reply_command_complete(command, struct.pack("<BHH", 0, handle, 0))
            if ocf == 0x0002:  # Reset_Failed_Contact_Counter -> Status(0), Handle(2B)
                return self._reply_command_complete(command, struct.pack("<BH", 0, handle))
            if ocf == 0x0003:  # Read_Link_Quality -> Status(0), Handle(2B), LinkQuality(255)
                return self._reply_command_complete(command, struct.pack("<BHB", 0, handle, 255))
            if ocf == 0x0005:  # Read_RSSI -> Status(0), Handle(2B), RSSI(-40 dBm)
                return self._reply_command_complete(command, struct.pack("<BHb", 0, handle, -40))
            if ocf == 0x0006:  # Read_AFH_Channel_Map -> Status(0), Handle(2B), Mode(0), Map(10B)
                return self._reply_command_complete(command, struct.pack("<BHB10s", 0, handle, 0, b"\xff" * 9 + b"\x7f"))
            if ocf == 0x0008:  # Read_Encryption_Key_Size -> Status(0), Handle(2B), KeySize(16)
                return self._reply_command_complete(command, struct.pack("<BHB", 0, handle, 16))

        logger.warning("Unhandled HCI command OGF=0x%02X OCF=0x%04X (%s)", ogf, ocf, command)
        if isinstance(command, hci.HCI_SyncCommand):
            return super().on_hci_command(command)
        self._send_hci_command_status(hci.HCI_ErrorCode.UNKNOWN_HCI_COMMAND_ERROR, command.op_code)
        return None
