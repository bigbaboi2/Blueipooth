"""Dual-MAC Virtual Headset Manager (Stereo A2DP Sink + Independent HD Mic HFP) on Bumble LocalLink."""
from __future__ import annotations

import asyncio
import hashlib
import logging
import struct
from typing import Any, Awaitable, Callable

from bumble import a2dp, avdtp, controller, device, hci, hfp, host, l2cap, link, pairing, rfcomm

from virtual_controller import BlueipoothVirtualController, VirtualClassicPeerInfo

logger = logging.getLogger(__name__)


def _install_windows_l2cap_config_compat() -> None:
    """
    Windows BthA2dp.sys includes standard L2CAP FLUSH_TIMEOUT (0x02) and QOS (0x03)
    options when configuring the A2DP RTP media channel. Stock Bumble rejects them
    with FAILURE_UNKNOWN_OPTIONS, causing Windows to immediately tear down A2DP.
    """
    if getattr(l2cap.ClassicChannel, "_blueipooth_patched", False):
        return

    orig_on_configure_request = l2cap.ClassicChannel.on_configure_request

    def _compat_on_configure_request(
        self: l2cap.ClassicChannel, request: l2cap.L2CAP_Configure_Request
    ) -> None:
        pt = l2cap.L2CAP_Configure_Request.ParameterType
        decoded = l2cap.L2CAP_Control_Frame.decode_configuration_options(request.options)
        if not any(
            opt[0] in (pt.FLUSH_TIMEOUT, pt.QOS, pt.EXTENDED_FLOW_SPEC)
            for opt in decoded
        ):
            orig_on_configure_request(self, request)
            return

        if self.state not in (
            self.State.WAIT_CONFIG,
            self.State.WAIT_CONFIG_REQ,
            self.State.WAIT_CONFIG_REQ_RSP,
        ):
            orig_on_configure_request(self, request)
            return

        filtered = [
            opt
            for opt in decoded
            if opt[0] not in (pt.FLUSH_TIMEOUT, pt.QOS, pt.EXTENDED_FLOW_SPEC)
        ]
        sanitized_req = l2cap.L2CAP_Configure_Request(
            identifier=request.identifier,
            destination_cid=request.destination_cid,
            flags=request.flags,
            options=l2cap.L2CAP_Control_Frame.encode_configuration_options(filtered),
        )
        orig_on_configure_request(self, sanitized_req)

    l2cap.ClassicChannel.on_configure_request = _compat_on_configure_request  # type: ignore[method-assign]
    setattr(l2cap.ClassicChannel, "_blueipooth_patched", True)


_install_windows_l2cap_config_compat()


def derive_dual_macs(base_identifier: str) -> tuple[str, str]:
    """Derive deterministic, locally-administered Bluetooth Public Addresses for Stereo & Mic."""
    digest = hashlib.sha256(base_identifier.encode("utf-8")).digest()
    # Stereo MAC: B1:7E:A2:xx:yy:01, Mic MAC: B1:7E:FF:xx:yy:02
    b3, b4 = digest[0], digest[1]
    stereo_mac = f"B1:7E:A2:{b3:02X}:{b4:02X}:01"
    mic_mac = f"B1:7E:FF:{b3:02X}:{b4:02X}:02"
    return stereo_mac, mic_mac


class _InMemoryHciBridge:
    """Direct zero-copy HCI packet bridge between an in-process Bumble Host and Controller."""

    def __init__(self, ctrl: controller.Controller, dev_host: host.Host) -> None:
        self.ctrl = ctrl
        self.dev_host = dev_host
        self.terminated: asyncio.Future[None] = (
            asyncio.get_running_loop().create_future()
        )
        ctrl.host_sink = self._ControllerToHostSink(dev_host)
        dev_host.hci_sink = self._HostToControllerSink(ctrl)

    class _ControllerToHostSink:
        def __init__(self, dev_host: host.Host) -> None:
            self._host = dev_host

        def on_packet(self, packet: bytes) -> None:
            self._host.on_packet(packet)

    class _HostToControllerSink:
        def __init__(self, ctrl: controller.Controller) -> None:
            self._ctrl = ctrl

        def on_packet(self, packet: bytes) -> None:
            self._ctrl.on_packet(packet)


class DualMacVirtualHeadsetManager:
    """
    Synthesizes TWO independent Bluetooth devices on the Virtual Radio Link whenever
    an AirPods / Bluetooth Headset is connected to the companion Android phone:
      1) '<Name> (Blueipooth Stereo)' -> Pure A2DP Sink (0x110B) so Windows NEVER downgrades audio!
      2) '<Name> (Blueipooth HD Mic)' -> Independent HFP Hands-Free (0x111E) wireless microphone!
    """

    def __init__(
        self,
        virtual_link: link.LocalLink,
        main_controller: BlueipoothVirtualController,
        ipc_emit: Callable[[dict[str, Any]], Awaitable[None]] | None = None,
    ) -> None:
        self.virtual_link = virtual_link
        self.main_controller = main_controller
        self.ipc_emit = ipc_emit
        self.audio_mode: str = "GamingOpus"  # "GamingOpus" | "NativeA2dp"

        self.stereo_device: device.Device | None = None
        self.stereo_controller: controller.Controller | None = None
        self.mic_device: device.Device | None = None
        self.mic_controller: controller.Controller | None = None
        self.current_headset_name: str | None = None
        self.stereo_mac: str | None = None
        self.mic_mac: str | None = None

    async def attach_headset(
        self, name: str, remote_mac: str = "", has_mic: bool = True
    ) -> tuple[str, str | None]:
        """Create and power-on Dual-MAC virtual Bluetooth headset peers."""
        await self.detach_headset()

        clean_name = (name or "Bluetooth Headset").strip()[:40]
        self.current_headset_name = clean_name
        stereo_mac, mic_mac = derive_dual_macs(remote_mac or clean_name)
        self.stereo_mac = stereo_mac

        # 1. Spin up Stereo A2DP Sink Peer
        stereo_full_name = f"{clean_name} (Blueipooth Stereo)"
        self.stereo_controller, self.stereo_device = await self._create_peer_device(
            name=stereo_full_name,
            mac=stereo_mac,
            class_of_device=0x240418,  # Audio + Rendering + Headphones
        )
        self._configure_a2dp_sink(self.stereo_device)
        self.main_controller.register_classic_peer(
            VirtualClassicPeerInfo(
                address=hci.Address(stereo_mac, hci.Address.PUBLIC_DEVICE_ADDRESS),
                name=stereo_full_name,
                class_of_device=0x240418,
                service_uuids_16=[0x110B],  # Pure A2DP Audio Sink
            )
        )

        # 2. Spin up Independent HD Mic Peer (prevents Windows HFP mono downgrade!)
        if has_mic:
            self.mic_mac = mic_mac
            mic_full_name = f"{clean_name} (Blueipooth HD Mic)"
            self.mic_controller, self.mic_device = await self._create_peer_device(
                name=mic_full_name,
                mac=mic_mac,
                class_of_device=0x200408,  # Audio + Hands-free
            )
            self._configure_hfp_handsfree(self.mic_device)
            self.main_controller.register_classic_peer(
                VirtualClassicPeerInfo(
                    address=hci.Address(mic_mac, hci.Address.PUBLIC_DEVICE_ADDRESS),
                    name=mic_full_name,
                    class_of_device=0x200408,
                    service_uuids_16=[0x111E, 0x1203],  # Handsfree, Generic Audio
                )
            )
        else:
            self.mic_mac = None

        logger.info(
            "Attached Dual-MAC Headset '%s': Stereo=%s, Mic=%s",
            clean_name,
            self.stereo_mac,
            self.mic_mac,
        )
        return self.stereo_mac, self.mic_mac

    async def detach_headset(self) -> None:
        if self.stereo_mac:
            self.main_controller.unregister_classic_peer(self.stereo_mac)
        if self.mic_mac:
            self.main_controller.unregister_classic_peer(self.mic_mac)

        for dev, ctrl in [
            (self.stereo_device, self.stereo_controller),
            (self.mic_device, self.mic_controller),
        ]:
            if dev is not None:
                try:
                    await dev.power_off()
                except Exception:
                    pass
            if ctrl is not None:
                self.virtual_link.remove_controller(ctrl)

        self.stereo_device = None
        self.stereo_controller = None
        self.mic_device = None
        self.mic_controller = None
        self.current_headset_name = None
        self.stereo_mac = None
        self.mic_mac = None

    async def _create_peer_device(
        self, name: str, mac: str, class_of_device: int
    ) -> tuple[controller.Controller, device.Device]:
        # Keep the synthesized headset controllers on the same Windows-compatible
        # command handler as the exported adapter. Windows sends the Secure
        # Connections Host Support opcode while setting up a BthUsb radio; the
        # stock Controller answers UNKNOWN_HCI_COMMAND_ERROR and can abort pairing.
        ctrl = BlueipoothVirtualController(
            name=f"ctrl-{name}",
            virtual_link=self.virtual_link,
            public_address=mac,
        )
        ctrl.lmp_features = self.main_controller.lmp_features
        ctrl.acl_data_packet_length = 1021

        dev_host = host.Host(controller_source=ctrl, controller_sink=ctrl)

        dev = device.Device(
            name=name,
            address=hci.Address(mac, hci.Address.PUBLIC_DEVICE_ADDRESS),
            host=dev_host,
        )
        dev.classic_enabled = True
        dev.le_enabled = False
        dev.classic_sc_enabled = True
        dev.class_of_device = class_of_device
        dev.pairing_config_factory = lambda _conn: pairing.PairingConfig(
            sc=True,
            mitm=False,
            bonding=True,
            delegate=pairing.PairingDelegate(
                io_capability=pairing.PairingDelegate.NO_OUTPUT_NO_INPUT
            ),
        )

        await dev.power_on()
        await dev.set_discoverable(True)
        await dev.set_connectable(True)
        return ctrl, dev

    def _configure_a2dp_sink(self, dev: device.Device) -> None:
        # Register SDP Audio Sink record (handle 0x00010001)
        dev.sdp_service_records[0x00010001] = a2dp.make_audio_sink_service_sdp_records(
            0x00010001
        )
        sbc_info = a2dp.SbcMediaCodecInformation(
            sampling_frequency=(
                a2dp.SbcMediaCodecInformation.SamplingFrequency.SF_48000
                | a2dp.SbcMediaCodecInformation.SamplingFrequency.SF_44100
            ),
            channel_mode=(
                a2dp.SbcMediaCodecInformation.ChannelMode.JOINT_STEREO
                | a2dp.SbcMediaCodecInformation.ChannelMode.STEREO
                | a2dp.SbcMediaCodecInformation.ChannelMode.MONO
            ),
            block_length=(
                a2dp.SbcMediaCodecInformation.BlockLength.BL_16
                | a2dp.SbcMediaCodecInformation.BlockLength.BL_12
                | a2dp.SbcMediaCodecInformation.BlockLength.BL_8
                | a2dp.SbcMediaCodecInformation.BlockLength.BL_4
            ),
            subbands=(
                a2dp.SbcMediaCodecInformation.Subbands.S_8
                | a2dp.SbcMediaCodecInformation.Subbands.S_4
            ),
            allocation_method=(
                a2dp.SbcMediaCodecInformation.AllocationMethod.LOUDNESS
                | a2dp.SbcMediaCodecInformation.AllocationMethod.SNR
            ),
            minimum_bitpool_value=2,
            maximum_bitpool_value=53,
        )
        codec_caps = avdtp.MediaCodecCapabilities(
            media_type=avdtp.AVDTP_AUDIO_MEDIA_TYPE,
            media_codec_type=a2dp.A2DP_SBC_CODEC_TYPE,
            media_codec_information=sbc_info,
        )
        listener = avdtp.Listener.for_device(dev)

        def on_avdtp_connection(server: avdtp.Protocol) -> None:
            logger.info("Windows connected AVDTP A2DP signaling to virtual Stereo headset")
            sink = server.add_sink(codec_caps)
            logged_stream = False

            def on_rtp_packet(_packet: avdtp.MediaPacket) -> None:
                nonlocal logged_stream
                if not logged_stream:
                    logged_stream = True
                    logger.info("Windows A2DP RTP media stream active")

            sink.on("rtp_packet", on_rtp_packet)

        listener.on("connection", on_avdtp_connection)

    def _configure_hfp_handsfree(self, dev: device.Device) -> None:
        rfcomm_server = rfcomm.Server(dev)
        hf_config = hfp.HfConfiguration(
            supported_hf_features=[
                hfp.HfFeature.CODEC_NEGOTIATION,
                hfp.HfFeature.REMOTE_VOLUME_CONTROL,
                hfp.HfFeature.EC_NR,
            ],
            supported_hf_indicators=[],
            supported_audio_codecs=[
                hfp.AudioCodec.MSBC,
                hfp.AudioCodec.CVSD,
            ],
        )

        def on_dlc(dlc: rfcomm.DLC) -> None:
            logger.info("Windows opened HFP RFCOMM DLC on HD Mic peer")
            hfp.HfProtocol(dlc, hf_config)

        channel_num = rfcomm_server.listen(on_dlc)
        dev.sdp_service_records[0x00010002] = hfp.make_hf_sdp_records(
            0x00010002, channel_num, hf_config
        )
