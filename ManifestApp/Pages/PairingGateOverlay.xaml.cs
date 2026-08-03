using Microsoft.UI.Xaml.Controls;

namespace ManifestApp.Pages;

public sealed partial class PairingGateOverlay : UserControl
{
    public event EventHandler? PairingCompleted;

    public PairingGateOverlay()
    {
        InitializeComponent();
        PairingPanel.PairingCompleted += (_, _) => PairingCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh()
    {
        PairingPanel.RefreshKeyStatus();
    }
}
