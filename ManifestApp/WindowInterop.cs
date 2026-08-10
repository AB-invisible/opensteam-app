using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ManifestApp;

internal static class WindowInterop
{
    internal static nint GetWindowHandle(Window window) => WindowNative.GetWindowHandle(window);
}
