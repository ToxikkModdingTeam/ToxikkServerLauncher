namespace ToxikkServerLauncher
{
  interface ILauncher
  {
    string ToxikkFolder { get; }
    string WorkshopFolder { get; }
    string HttpFolder { get; }
    string SteamcmdExe { get; }
    bool UpdateToxikk { get; }
    bool CleanWorkshop { get; }
    bool SyncWorkshop { get; }
    IniFile MainIni { get; }
    string MachineName { get; }

    void CopyFolder(string sourceDir, string targetDir);
  }
}
