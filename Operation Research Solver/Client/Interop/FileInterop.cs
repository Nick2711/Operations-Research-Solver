// Client/Interop/FileInterop.cs
using Microsoft.JSInterop;

namespace Operation_Research_Solver.Client.Interop;

// Static so JS can call it without an instance.
// We expose a .NET event your page can subscribe to.
public static class FileInterop
{
    public static event Action<string, string>? ModelLoaded;

    // JS will call this when a file is read on the client
    [JSInvokable]
    public static Task OnModelLoaded(string fileName, string content)
    {
        ModelLoaded?.Invoke(fileName, content);
        return Task.CompletedTask;
    }
}
