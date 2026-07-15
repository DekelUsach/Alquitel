namespace Alquitel.Mobile;

public partial class App : Application
{
    public App(AppShell appShell)
    {
        InitializeComponent();
        UserAppTheme = Preferences.Get("app_theme", "dark") == "light" ? AppTheme.Light : AppTheme.Dark;
        MainPage = appShell;
    }
}
