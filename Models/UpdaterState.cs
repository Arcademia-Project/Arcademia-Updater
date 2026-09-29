namespace Research_Arcade_Updater.Models
{
    public enum UpdaterState
    {
        idle,
        startingLauncher,
        closingLauncher,
        restartingLauncher,
        failed,
        checkingForUpdates,
        updatingLauncher,
        waitingOnInternet,
        repairingLauncher,
        retryingLauncher,
    }
}
