namespace ManifestApp.Pages;

/// <summary>
/// Passed when navigating to <see cref="GameRequestsPage"/>.
/// When <see cref="AppId"/> is non-null the page pre-selects that game
/// (used by the install-failure flow on the Home page).
/// </summary>
public sealed record GameRequestsNavigationArgs(uint? AppId = null, string? GameName = null);
