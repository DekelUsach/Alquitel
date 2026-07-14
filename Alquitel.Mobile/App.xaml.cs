namespace Alquitel.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = Preferences.Get("app_theme", "dark") == "light" ? AppTheme.Light : AppTheme.Dark;
        MainPage = new AppShell();
    }
}
