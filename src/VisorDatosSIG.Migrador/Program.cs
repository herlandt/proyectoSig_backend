namespace VisorDatosSIG.Migrador;

internal static class Program
{
    /// <summary>Migrador SHP → SQL Server de VisorDatosSIG (aplicación de escritorio).</summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) =>
            MessageBox.Show($"Ocurrió un error inesperado:\n{e.Exception.Message}", "VisorDatosSIG · Migrador",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        System.Windows.Forms.Application.Run(new FormularioPrincipal());
    }
}
