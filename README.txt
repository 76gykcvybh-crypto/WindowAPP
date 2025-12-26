VfdWpfApp (WPF .NET 8)

What it does (MVP):
- Connect to a USB-to-UART COM port.
- Send custom "Modbus-like" frames:
  - 0x03 Read Holding Registers (1..16 words)
  - 0x06 Write Single Register (1 word)
- Enforces:
  - One outstanding request at a time.
  - Min inter-command gap (InterCommandDelayMs).
  - Timeout (ResponseTimeoutMs). Device is known to be silent on errors and drops fast commands.
- UI:
  - Connection settings
  - Quick control: Start/Stop/Shutdown + Set speed (0x000A raw u16)
  - Parameters tab: load tac_cac3x_parameters.json, select parameter, read/write u16
  - Console log tab: shows TX/RX and writes Logs\comm_*.jsonl

How to run:
1) Open VfdWpfApp.sln in Visual Studio 2022
2) Build & Run
3) Select COM and settings then Connect
4) Use Quick Control or Parameters

Notes:
- Device address is fixed at 0x01 in VfdProtocol.cs
- CRC is Modbus CRC16, low-byte first.
