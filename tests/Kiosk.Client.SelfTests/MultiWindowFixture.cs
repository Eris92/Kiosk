using System.IO.Pipes;

namespace Kiosk.Client;

internal static class MultiWindowFixture
{
    internal static int Run(string name)
    {
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out))
        {
            try
            {
                client.Connect(200);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine("new"); return 0;
            }
            catch (TimeoutException) { }
        }
        using var lifetime = new CancellationTokenSource();
        using var context = new ApplicationContext();
        var windows = new List<Form>();
        void Open()
        {
            var form = new Form { Text = "Kiosk multi-window fixture " + windows.Count, Size = new Size(480, 320) };
            windows.Add(form);
            form.FormClosed += (_, _) => { windows.Remove(form); if (windows.Count == 0) context.ExitThread(); };
            form.Show();
        }
        Open();
        var dispatch = windows[0];
        _ = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(lifetime.Token);
                    using var reader = new StreamReader(server);
                    if (await reader.ReadLineAsync(lifetime.Token) == "new") dispatch.BeginInvoke(Open);
                }
            }
            catch (OperationCanceledException) { }
        });
        Application.Run(context);
        lifetime.Cancel();
        return 0;
    }
}
