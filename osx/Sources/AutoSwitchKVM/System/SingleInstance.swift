import Foundation

/// Only one copy of the app may run: two would both drive the same Bluetooth devices and fight
/// (one pairs while the other unpairs). That's easy to hit once there's an installed copy and a
/// dev build, or an old copy still running during an update.
///
/// An exclusive `flock` on `~/Library/Application Support/AutoSwitchKVM/instance.lock`, held for
/// the process's lifetime. The system drops it when the process exits or crashes, so a stale lock
/// can't block the next launch (unlike a PID file).
@MainActor
enum SingleInstance {
    private static var lockFD: Int32 = -1

    /// True when this process holds the lock. Also true when the lock can't be taken for another
    /// reason (unwritable folder): better to run than to refuse on a guess.
    /// - Parameter directory: where `instance.lock` lives; tests pass a temporary folder.
    static func claim(in directory: URL? = nil) -> Bool {
        let dir =
            directory
            ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("AutoSwitchKVM", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        } catch {
            Log.app.warn("single-instance check skipped: cannot create \(dir.path): \(error)")
            return true
        }
        let path = dir.appendingPathComponent("instance.lock").path
        let fd = open(path, O_CREAT | O_RDWR, 0o644)
        guard fd >= 0 else {
            Log.app.warn("single-instance check skipped: cannot open \(path) (errno \(errno))")
            return true
        }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
            let busy = errno == EWOULDBLOCK
            close(fd)
            if !busy { Log.app.warn("single-instance check skipped: flock failed (errno \(errno))") }
            return !busy
        }
        lockFD = fd  // keep it open: closing it would release the lock
        let pid = "\(getpid())\n"
        ftruncate(fd, 0)
        _ = pid.withCString { write(fd, $0, strlen($0)) }
        return true
    }
}
