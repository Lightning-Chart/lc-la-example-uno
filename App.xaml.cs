namespace LightningChartUnoExample;

public sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainWindow = new Window { Content = new MainPage() };
    }
}
