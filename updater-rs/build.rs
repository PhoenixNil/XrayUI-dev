// Embeds the Win32 application manifest (asInvoker) into the updater binary.
// Mirrors XrayUI.Updater/app.manifest: we start un-elevated and self-elevate at
// runtime when the install dir actually needs admin rights. The explicit
// asInvoker level is what keeps Windows' installer-detection heuristic from
// force-prompting UAC just because the file name contains "update".
//
// Also embeds a version resource. SignPath's artifact configuration requires
// ProductName = "XrayUI-dev" and ProductVersion = <release version> on every
// signed exe, and both must match what XrayUI-dev.csproj gives the main exe.
// The release version arrives in XRAYUI_VERSION (release.yml and the csproj's
// PublishUpdaterAlongside target set it); plain `cargo build` falls back to the
// crate version. winresource compiles the resource with the Windows SDK's rc.exe.
use embed_manifest::manifest::ExecutionLevel;
use embed_manifest::{embed_manifest, new_manifest};
use winresource::{VersionInfo, WindowsResource};

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-env-changed=XRAYUI_VERSION");

    if std::env::var_os("CARGO_CFG_WINDOWS").is_none() {
        return;
    }

    embed_manifest(
        new_manifest("XrayUI.Updater").requested_execution_level(ExecutionLevel::AsInvoker),
    )
    .expect("unable to embed manifest file");

    let version = std::env::var("XRAYUI_VERSION")
        .ok()
        .map(|v| v.trim().to_string())
        .filter(|v| !v.is_empty())
        .unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_string());

    // "1.16" -> 1.16.0.0; a "-dev"/"+sha" suffix only survives in the string form.
    let mut quad = [0u64; 4];
    let numeric = version.split(['-', '+']).next().unwrap_or("");
    for (slot, part) in quad.iter_mut().zip(numeric.split('.')) {
        *slot = part.parse::<u16>().unwrap_or(0).into();
    }
    let packed = quad[0] << 48 | quad[1] << 32 | quad[2] << 16 | quad[3];

    WindowsResource::new()
        .set_language(0x0409) // en-US
        .set_version_info(VersionInfo::FILEVERSION, packed)
        .set_version_info(VersionInfo::PRODUCTVERSION, packed)
        .set("ProductName", "XrayUI-dev")
        .set("ProductVersion", &version)
        .set(
            "FileVersion",
            &format!("{}.{}.{}.{}", quad[0], quad[1], quad[2], quad[3]),
        )
        .set("FileDescription", "XrayUI Updater")
        .set("CompanyName", "PhoenixNil")
        .set("LegalCopyright", "Copyright (c) PhoenixNil")
        .set("InternalName", "XrayUI.Updater.exe")
        .set("OriginalFilename", "XrayUI.Updater.exe")
        .compile()
        .expect("unable to compile version resource");
}
