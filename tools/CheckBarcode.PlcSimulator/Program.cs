using System.Net;
using CheckBarcode.Simulator;

// PLC simulator for FAT / demos: CheckBarcode.PlcSimulator.exe [--port 502] [--any]
var port = 502;
var any = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
    if (args[i] == "--any") any = true;
}

using var server = new ModbusTcpServer(port, any ? IPAddress.Any : IPAddress.Loopback);
server.Start();
using var machine = new MachineModel(server);
Console.WriteLine($"CheckBarcode PLC simulator listening on port {server.Port}");
Console.WriteLine("Keys: R run/stop · E e-stop · 1-6 toggle alarm bits · C change cam 0 locally · +/- speed offset · T/G temperature offset ± · H heartbeat on/off · Q quit");

if (Console.IsInputRedirected)
{
    // Running as a service / in CI: no keyboard, just serve until killed.
    await Task.Delay(Timeout.Infinite);
}

var alarms = new bool[6];
var estop = false;
while (true)
{
    var key = Console.ReadKey(intercept: true).KeyChar;
    switch (char.ToUpperInvariant(key))
    {
        case 'Q': return;
        case 'R': machine.Running = !machine.Running; Console.WriteLine($"Running = {machine.Running}"); break;
        case 'E': estop = !estop; machine.SetEStop(estop); Console.WriteLine($"E-stop = {estop}"); break;
        case 'H': machine.HeartbeatEnabled = !machine.HeartbeatEnabled; Console.WriteLine($"Heartbeat = {machine.HeartbeatEnabled}"); break;
        case 'C': machine.ChangeCamLocally(0, (ushort)Random.Shared.Next(0, 360)); Console.WriteLine("Cam 0 changed on the machine"); break;
        case '+': machine.SpeedOffset += 10; Console.WriteLine($"Speed offset = {machine.SpeedOffset}"); break;
        case '-': machine.SpeedOffset -= 10; Console.WriteLine($"Speed offset = {machine.SpeedOffset}"); break;
        case 'T': machine.TempOffset += 5; Console.WriteLine($"Temperature offset = {machine.TempOffset}"); break;
        case 'G': machine.TempOffset -= 5; Console.WriteLine($"Temperature offset = {machine.TempOffset}"); break;
        case >= '1' and <= '6':
            var bit = key - '1';
            alarms[bit] = !alarms[bit];
            machine.SetAlarm(bit, alarms[bit]);
            Console.WriteLine($"Alarm bit {bit} = {alarms[bit]}");
            break;
    }
}
