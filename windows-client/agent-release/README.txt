IEMAS Agent (Inquiry Supervision build)
=======================================

Needs once on the PC: .NET 8 Desktop Runtime (x64)
  https://dotnet.microsoft.com/download/dotnet/8.0  ->  ".NET Desktop Runtime 8.0.x" -> Windows x64
  or:  winget install Microsoft.DotNet.DesktopRuntime.8

Run:  IemasAgent.exe
Settings -> Server URL:  http://172.16.2.68:8190

Update:
  1. Right-click the IEMAS tray icon -> Exit
  2. git pull
  3. Start IemasAgent.exe again (registration is kept)
