//! XrayUI.Updater — standalone post-download updater.
//!
//! Rust port of `XrayUI.Updater/Program.cs`. Invoked by the main app after a new
//! release has been downloaded, verified and extracted. It:
//!   1. waits for the parent (main app) process to exit,
//!   2. copies the extracted files over the install directory (self-elevating
//!      only if that directory isn't writable),
//!   3. cleans up the staging dirs,
//!   4. relaunches the new app — unelevated, even if we had to elevate to copy.
//!
//! CLI contract — must stay byte-compatible with `Services/UpdateService.cs`:
//!   --parent-pid=N --extracted-dir=PATH --install-dir=PATH --launch-after=NAME
//!   [--elevated]

use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::thread;
use std::time::Duration;

use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_CANCELLED, HANDLE, SYSTEMTIME, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::Security::{
    GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY,
};
use windows_sys::Win32::System::SystemInformation::GetLocalTime;
use windows_sys::Win32::System::Threading::{
    GetCurrentProcess, OpenProcess, OpenProcessToken, WaitForSingleObject, PROCESS_SYNCHRONIZE,
};
use windows_sys::Win32::UI::Shell::{ShellExecuteExW, SHELLEXECUTEINFOW};

const PARENT_PID_ARG: &str = "--parent-pid=";
const EXTRACTED_DIR_ARG: &str = "--extracted-dir=";
const INSTALL_DIR_ARG: &str = "--install-dir=";
const LAUNCH_AFTER_ARG: &str = "--launch-after=";
const ELEVATED_FLAG: &str = "--elevated";

const COPY_RETRY_COUNT: u32 = 5;
const COPY_RETRY_DELAY_MS: u64 = 200;
const PARENT_EXIT_TIMEOUT_MS: u32 = 15_000;

fn main() {
    std::process::exit(run());
}

fn run() -> i32 {
    // args[0] is our own exe; the contract args follow.
    let raw_args: Vec<String> = std::env::args().skip(1).collect();

    let mut parent_pid: Option<u32> = None;
    let mut extracted_dir: Option<String> = None;
    let mut install_dir: Option<String> = None;
    let mut launch_after: Option<String> = None;
    let mut elevated = false;

    for a in &raw_args {
        if let Some(v) = strip_prefix_ci(a, PARENT_PID_ARG) {
            parent_pid = v.parse::<u32>().ok();
        } else if let Some(v) = strip_prefix_ci(a, EXTRACTED_DIR_ARG) {
            extracted_dir = Some(v.to_string());
        } else if let Some(v) = strip_prefix_ci(a, INSTALL_DIR_ARG) {
            install_dir = Some(v.to_string());
        } else if let Some(v) = strip_prefix_ci(a, LAUNCH_AFTER_ARG) {
            launch_after = Some(v.to_string());
        } else if a.eq_ignore_ascii_case(ELEVATED_FLAG) {
            elevated = true;
        }
    }

    let (parent_pid, extracted_dir, install_dir, launch_after) =
        match (parent_pid, extracted_dir, install_dir, launch_after) {
            (Some(p), Some(e), Some(i), Some(l))
                if !e.is_empty() && !i.is_empty() && !l.is_empty() =>
            {
                (p, e, i, l)
            }
            _ => {
                eprintln!(
                    "Usage: XrayUI.Updater --parent-pid=N --extracted-dir=PATH \
                     --install-dir=PATH --launch-after=NAME [--elevated]"
                );
                return 2;
            }
        };

    let mut log = Logger::open();
    log.log(&format!(
        "Updater started. parent={parent_pid}, elevated={elevated}"
    ));
    log.log(&format!("  extracted-dir = {extracted_dir}"));
    log.log(&format!("  install-dir   = {install_dir}"));
    log.log(&format!("  launch-after  = {launch_after}"));

    wait_for_parent_exit(parent_pid, &mut log);

    let install_path = Path::new(&install_dir);
    if !try_ensure_writable(install_path, &mut log) {
        if !elevated {
            log.log("Install dir not writable; relaunching elevated…");
            if relaunch_elevated(&raw_args, &mut log) {
                return 0; // the elevated instance takes over from here
            }
            // Elevation launch failed — most commonly the user declined UAC. The
            // copy never ran, so the OLD app is still intact in the install dir;
            // relaunch it so the user isn't stranded with a vanished app.
            log.log("Elevation declined or failed; relaunching existing app without updating.");
            let existing_exe = install_path.join(&launch_after);
            match launch_app(&existing_exe, install_path, &mut log) {
                Ok(()) => log.log("Existing app relaunched (update skipped)."),
                Err(e) => log.log(&format!("Failed to relaunch existing app: {e}")),
            }
            return 6; // update could not proceed without elevation
        }
        log.log("Install dir still not writable after elevation. Aborting.");
        return 3;
    }

    if let Err(e) = copy_overwrite(Path::new(&extracted_dir), install_path, &mut log) {
        // copy_overwrite logs the precise failing file; mirror Program.cs's
        // top-level catch which also records a "Fatal" line and returns 1.
        log.log(&format!("Fatal: {e}"));
        return 1;
    }
    log.log("Copy complete.");

    let new_exe = install_path.join(&launch_after);
    if !new_exe.exists() {
        log.log(&format!(
            "New app exe not found after update: {}. Aborting launch.",
            new_exe.display()
        ));
        return 4;
    }

    cleanup_large_staging_dirs(Path::new(&extracted_dir), &mut log);

    // Launch the new app unelevated. Even if we elevated to do the file overwrite,
    // the app itself should run under the user's normal token.
    match launch_app(&new_exe, install_path, &mut log) {
        Ok(()) => log.log("New app launched."),
        Err(e) => {
            log.log(&format!("Failed to launch new app: {e}"));
            return 5;
        }
    }

    0
}

/// Block until the parent process exits (or the timeout elapses). A missing
/// process — already gone — is the common, healthy case.
fn wait_for_parent_exit(pid: u32, log: &mut Logger) {
    unsafe {
        let handle: HANDLE = OpenProcess(PROCESS_SYNCHRONIZE, 0, pid);
        if handle.is_null() {
            // Parent already exited (or not accessible) — nothing to wait for.
            return;
        }
        let result = WaitForSingleObject(handle, PARENT_EXIT_TIMEOUT_MS);
        CloseHandle(handle);

        if result == WAIT_TIMEOUT {
            log.log(&format!(
                "Parent {pid} did not exit within {PARENT_EXIT_TIMEOUT_MS} ms; continuing anyway."
            ));
        } else if result != WAIT_OBJECT_0 {
            log.log(&format!(
                "WaitForParentExit: unexpected wait result {result} for pid {pid}."
            ));
        }
    }
}

/// Probe the install dir for write access by creating and deleting a marker file.
fn try_ensure_writable(install_dir: &Path, log: &mut Logger) -> bool {
    let probe = install_dir.join(".xrayui-write-test");
    match File::create(&probe).and_then(|_| fs::remove_file(&probe)) {
        Ok(()) => true,
        Err(e) => {
            log.log(&format!("Write probe denied: {e}"));
            false
        }
    }
}

/// Re-launch ourselves with the UAC "runas" verb, re-passing every original arg
/// plus the --elevated marker so the elevated instance doesn't loop. Returns true
/// if the elevated process was started, false if the launch failed (commonly: the
/// user declined the UAC prompt).
fn relaunch_elevated(original_args: &[String], log: &mut Logger) -> bool {
    let exe = std::env::current_exe()
        .ok()
        .and_then(|p| p.to_str().map(str::to_string))
        .unwrap_or_else(|| "XrayUI.Updater.exe".to_string());

    let mut quoted: Vec<String> = original_args.iter().map(|a| quote_arg(a)).collect();
    quoted.push(ELEVATED_FLAG.to_string());
    let params = quoted.join(" ");

    let verb = wide("runas");
    let file = wide(&exe);
    let parameters = wide(&params);

    unsafe {
        let mut sei: SHELLEXECUTEINFOW = core::mem::zeroed();
        sei.cbSize = size_of::<SHELLEXECUTEINFOW>() as u32;
        sei.lpVerb = verb.as_ptr();
        sei.lpFile = file.as_ptr();
        sei.lpParameters = parameters.as_ptr();
        sei.nShow = 1; // SW_SHOWNORMAL
        if ShellExecuteExW(&mut sei) == 0 {
            let err = GetLastError();
            if err == ERROR_CANCELLED {
                log.log("RelaunchElevated: user declined the UAC elevation prompt.");
            } else {
                log.log(&format!(
                    "RelaunchElevated: ShellExecuteExW(runas) failed (error {err})."
                ));
            }
            return false;
        }
        true
    }
}

/// Recursively copy every file from `source` onto `dest`, overwriting. We do NOT
/// auto-rollback: that would roughly double the upgrade's disk footprint, and most
/// failures here are transient/permission issues a re-run can resolve.
fn copy_overwrite(source: &Path, dest: &Path, log: &mut Logger) -> std::io::Result<()> {
    fs::create_dir_all(dest)?;

    let mut copied = 0usize;
    for src_file in enumerate_files(source)? {
        let rel = src_file.strip_prefix(source).unwrap_or(&src_file);
        let dst_file = dest.join(rel);

        if let Some(parent) = dst_file.parent() {
            fs::create_dir_all(parent)?;
        }

        if let Err(e) = copy_with_retry(&src_file, &dst_file, log) {
            log.log(&format!(
                "Copy aborted after {copied} files. Failed on '{}': {e}",
                rel.display()
            ));
            return Err(e);
        }
        copied += 1;
    }

    log.log(&format!("Copied {copied} files into {}", dest.display()));
    Ok(())
}

/// Copy one file, retrying transient IO/permission failures a few times.
fn copy_with_retry(src: &Path, dst: &Path, log: &mut Logger) -> std::io::Result<()> {
    let mut attempt = 0;
    loop {
        match fs::copy(src, dst) {
            Ok(_) => return Ok(()),
            Err(e) => {
                attempt += 1;
                if attempt >= COPY_RETRY_COUNT {
                    return Err(e);
                }
                log.log(&format!(
                    "Copy retry {}/{} for {}: {e}",
                    attempt,
                    COPY_RETRY_COUNT,
                    dst.display()
                ));
                thread::sleep(Duration::from_millis(COPY_RETRY_DELAY_MS));
            }
        }
    }
}

/// Depth-first collect every file (not directory) under `dir`.
fn enumerate_files(dir: &Path) -> std::io::Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        for entry in fs::read_dir(&d)? {
            let entry = entry?;
            let file_type = entry.file_type()?;
            if file_type.is_dir() {
                stack.push(entry.path());
            } else {
                out.push(entry.path());
            }
        }
    }
    Ok(out)
}

/// Delete the (potentially large) download + extracted staging dirs, best-effort.
fn cleanup_large_staging_dirs(extracted_dir: &Path, log: &mut Logger) {
    let Some(stage_root) = extracted_dir.parent() else {
        return;
    };
    delete_dir_best_effort(&stage_root.join("download"), log);
    delete_dir_best_effort(extracted_dir, log);
}

fn delete_dir_best_effort(path: &Path, log: &mut Logger) {
    if !path.exists() {
        return;
    }
    match fs::remove_dir_all(path) {
        Ok(()) => log.log(&format!("Deleted staging directory: {}", path.display())),
        Err(e) => log.log(&format!(
            "Could not delete staging directory '{}': {e}",
            path.display()
        )),
    }
}

/// Start the freshly-installed app. If we're elevated, bounce through Explorer so
/// the app comes back up under the user's normal (unelevated) token.
fn launch_app(exe_path: &Path, working_directory: &Path, log: &mut Logger) -> std::io::Result<()> {
    if is_elevated() {
        match Command::new("explorer.exe").arg(exe_path).spawn() {
            Ok(_) => return Ok(()),
            Err(e) => log.log(&format!("Unelevated launch via Explorer failed: {e}")),
        }
    }

    Command::new(exe_path)
        .current_dir(working_directory)
        .spawn()
        .map(|_| ())
}

fn is_elevated() -> bool {
    unsafe {
        let mut token: HANDLE = core::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return false;
        }

        let mut elevation = TOKEN_ELEVATION {
            TokenIsElevated: 0,
        };
        let mut ret_len: u32 = 0;
        let ok = GetTokenInformation(
            token,
            TokenElevation,
            (&mut elevation as *mut TOKEN_ELEVATION).cast(),
            size_of::<TOKEN_ELEVATION>() as u32,
            &mut ret_len,
        );
        CloseHandle(token);

        ok != 0 && elevation.TokenIsElevated != 0
    }
}

// ── small helpers ───────────────────────────────────────────────────────────

/// Case-insensitive prefix strip (ASCII), preserving the value's original case.
fn strip_prefix_ci<'a>(s: &'a str, prefix: &str) -> Option<&'a str> {
    let sb = s.as_bytes();
    let pb = prefix.as_bytes();
    if sb.len() >= pb.len() && sb[..pb.len()].eq_ignore_ascii_case(pb) {
        Some(&s[pb.len()..])
    } else {
        None
    }
}

/// Quote a single argument per the Windows CommandLineToArgvW rules.
fn quote_arg(arg: &str) -> String {
    let needs_quotes =
        arg.is_empty() || arg.contains([' ', '\t', '\n', '\u{0B}', '"']);
    if !needs_quotes {
        return arg.to_string();
    }

    let mut out = String::with_capacity(arg.len() + 2);
    out.push('"');
    let chars: Vec<char> = arg.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        let mut backslashes = 0;
        while i < chars.len() && chars[i] == '\\' {
            backslashes += 1;
            i += 1;
        }
        if i == chars.len() {
            // Trailing backslashes precede the closing quote → double them.
            out.extend(std::iter::repeat_n('\\', backslashes * 2));
        } else if chars[i] == '"' {
            out.extend(std::iter::repeat_n('\\', backslashes * 2 + 1));
            out.push('"');
            i += 1;
        } else {
            out.extend(std::iter::repeat_n('\\', backslashes));
            out.push(chars[i]);
            i += 1;
        }
    }
    out.push('"');
    out
}

/// UTF-16, NUL-terminated — for the Win32 *W APIs.
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

// ── logging ─────────────────────────────────────────────────────────────────

struct Logger {
    file: Option<File>,
}

impl Logger {
    fn open() -> Self {
        let file = (|| -> Option<File> {
            let local = std::env::var_os("LOCALAPPDATA")?;
            let mut path = PathBuf::from(local);
            path.push("XrayUI");
            path.push("Updates");
            fs::create_dir_all(&path).ok()?;
            path.push("updater.log");
            OpenOptions::new().create(true).append(true).open(&path).ok()
        })();
        Logger { file }
    }

    fn log(&mut self, msg: &str) {
        let line = format!("[{}] {msg}", now_timestamp());
        if let Some(f) = self.file.as_mut() {
            let _ = writeln!(f, "{line}");
            let _ = f.flush();
        }
        // Best-effort console echo; never panic if stdout is gone.
        let _ = writeln!(std::io::stdout(), "{line}");
    }
}

/// Local wall-clock timestamp matching Program.cs's "yyyy-MM-dd HH:mm:ss.fff".
fn now_timestamp() -> String {
    unsafe {
        let mut st: SYSTEMTIME = core::mem::zeroed();
        GetLocalTime(&mut st);
        format!(
            "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds
        )
    }
}
