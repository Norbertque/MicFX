using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace MicFX;

/// <summary>
/// Version, copyright and license information. The GPL asks interactive
/// programs to show a copyright notice, that there is no warranty, and how to
/// view the license. The license and the third-party notices are embedded in
/// the exe, so they are there offline and in the portable build too.
/// </summary>
public partial class AboutWindow : Window
{
    private const string LicenseResource = "MicFX.LICENSE.txt";
    private const string NoticesResource = "MicFX.THIRD-PARTY-NOTICES.txt";
    private static readonly string SourceUrl = $"https://github.com/{MainWindow.RepoSlug}";

    private string? shownResource;

    public AboutWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        txtVersion.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";
        runSource.Text = SourceUrl["https://".Length..];
    }

    private void License_Click(object sender, RoutedEventArgs e) => Toggle(LicenseResource);

    private void Notices_Click(object sender, RoutedEventArgs e) => Toggle(NoticesResource);

    /// <summary>Shows the document under the buttons; pressing the same button again hides it.</summary>
    private void Toggle(string resource)
    {
        if (shownResource == resource)
        {
            txtDocument.Visibility = Visibility.Collapsed;
            shownResource = null;
            return;
        }
        txtDocument.Text = ReadResource(resource);
        txtDocument.ScrollToHome();
        txtDocument.Visibility = Visibility.Visible;
        shownResource = resource;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(AboutWindow).Assembly.GetManifestResourceStream(name);
        if (stream == null) return $"Not included in this build. See {SourceUrl}";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Source_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(SourceUrl) { UseShellExecute = true });
}
