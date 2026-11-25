"""Newline-delimited JSON (NDJSON) IPC server between .NET Host and Bumble Engine."""
from __future__ import annotations

import asyncio
import json
import logging
from typing import Any, Awaitable, Callable

logger = logging.getLogger(__name__)


class ControlIpcServer:
    """Async TCP NDJSON server on 127.0.0.1:49553 for .NET <-> Bumble control messages."""

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 49553,
        on_message: Callable[[dict[str, Any]], Awaitable[None]] | None = None,
    ) -> None:
        self.host = host
        self.port = port
        self.on_message = on_message
        self._server: asyncio.AbstractServer | None = None
        self._writers: set[asyncio.StreamWriter] = set()

    async def start(self) -> int:
        self._server = await asyncio.start_server(
            self._handle_client, self.host, self.port
        )
        sockets = self._server.sockets
        if sockets:
            self.port = sockets[0].getsockname()[1]
        logger.info("Control IPC server listening on %s:%d", self.host, self.port)
        return self.port

    async def stop(self) -> None:
        for writer in list(self._writers):
            writer.close()
            try:
                await writer.wait_closed()
            except Exception:
                pass
        self._writers.clear()
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()
            self._server = None

    async def send_event(self, payload: dict[str, Any]) -> None:
        if not self._writers:
            return
        line = (json.dumps(payload, separators=(",", ":")) + "\n").encode("utf-8")
        stale: list[asyncio.StreamWriter] = []
        for writer in list(self._writers):
            try:
                writer.write(line)
                await writer.drain()
            except Exception:
                stale.append(writer)
        for writer in stale:
            self._writers.discard(writer)

    async def _handle_client(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter
    ) -> None:
        self._writers.add(writer)
        peer = writer.get_extra_info("peername")
        logger.info("Control IPC client connected: %s", peer)
        try:
            while not reader.at_eof():
                raw = await reader.readline()
                if not raw:
                    break
                text = raw.decode("utf-8", errors="replace").strip()
                if not text:
                    continue
                try:
                    msg = json.loads(text)
                except json.JSONDecodeError as exc:
                    logger.warning("Invalid JSON on IPC: %s", exc)
                    continue
                if self.on_message is not None:
                    await self.on_message(msg)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            logger.debug("IPC client %s closed: %s", peer, exc)
        finally:
            self._writers.discard(writer)
            writer.close()
