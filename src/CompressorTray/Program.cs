using System.Globalization;
using SystemCompressor.Tray;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// Unexpected errors are logged and the window keeps running (a repaint error must not end the app).
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception);
AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);

// Single instance: a second launch (e.g. a click on the pinned taskbar icon) brings the running window forward.
using var instance = new Mutex(true, @"Local\SystemCompressorApp", out var isFirstInstance);
using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SystemCompressorApp.Show");
if (!isFirstInstance)
{
    showEvent.Set();
    return;
}

try
{
    ApplicationConfiguration.Initialize();
    var form = new MainForm();
    var listener = new Thread(() =>
    {
        while (showEvent.WaitOne())
        {
            try { form.BeginInvoke(form.BringForward); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; }
        }
    }) { IsBackground = true };
    listener.Start();
    Application.Run(form);
}
catch (Exception ex)
{
    ErrorLog.Write(ex);
    MessageBox.Show($"System Compressor could not start.\n\n{ex.Message}\n\nDetails: {ErrorLog.FilePath}",
        "System Compressor", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
