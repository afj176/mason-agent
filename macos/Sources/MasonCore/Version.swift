/// Mason Agent's version on macOS. `scripts/version.sh` changes this line when a release is cut, and
/// `Scripts/build-app.sh` reads it for the app bundle, the zip and the installer.
public enum MasonVersion {
    public static let current = "2.2.2"
}
