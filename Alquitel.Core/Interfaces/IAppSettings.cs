namespace Alquitel.Core.Interfaces
{
    public interface IAppSettings
    {
        string PresupuestosFolder { get; set; }
        string PresupuestosTemplate { get; set; }
        string OfFolder { get; set; }
        string OfTemplate { get; set; }
        string OtFolder { get; set; }
        string OtTemplate { get; set; }
        bool IsDarkMode { get; set; }
        bool ExportPdf { get; set; }
        List<string> SmartSearchStopWords { get; set; }
        double SmartSearchThreshold { get; set; }
        double SmartSearchMargin { get; set; }
        void LoadSettings();
        void SaveSettings();
    }
}
