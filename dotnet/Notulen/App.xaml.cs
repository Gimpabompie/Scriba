using System;
using System.Windows;
using Notulen.Services;

namespace Notulen;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Verborgen "probe"-modus: test in dít losse proces of een modelbestand
        // laadbaar is. Crasht de native lader (AccessViolation, niet op te
        // vangen in .NET), dan sterft alleen dit hulpproces; de hoofd-app leest
        // de exitcode en geeft een nette melding i.p.v. mee te crashen.
        // Geen vensters/UI starten in deze modus.
        if (e.Args.Length >= 2 && e.Args[0] == "--probe-model")
        {
            int code = Transcriber.ProbeModelFile(e.Args[1]);
            Environment.Exit(code);
            return;
        }

        base.OnStartup(e);
    }
}
