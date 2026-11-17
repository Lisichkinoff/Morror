using System;
using System.Threading;
using Morror;

// Local receiver startup only; does not claim an actual iPhone connection.
class NativeIPhoneSmoke
{
    static int Main(string[] args)
    {
        try
        {
            using (var engine = new IPhoneMirrorEngine(args[0]))
            {
                engine.Initialize();
                Console.WriteLine("PASS bundled native API initialized");
                for (int run = 0; run < 2; run++)
                {
                    engine.Listen();
                    bool ready = false;
                    for (int i = 0; i < 100; i++)
                    {
                        if (engine.ReceiverReady()) { ready = true; break; }
                        Thread.Sleep(100);
                    }
                    if (!ready) throw new Exception("Receiver startup timed out");
                    Console.WriteLine("PASS receiver " + (run == 0 ? "startup" : "restart") +
                        "; discovered sending devices: " + engine.Devices(true, false).Length);
                    engine.StopListening();
                }
            }
            Console.WriteLine("PASS receiver cleanup; real phone streaming still requires a device test");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
