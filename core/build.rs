fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=../Cargo.toml");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }

    // SignPath restricts signing to WinPalm binaries with this release's metadata.
    let version = format!("{}.0", std::env::var("CARGO_PKG_VERSION").unwrap());
    winresource::WindowsResource::new()
        .set("ProductName", "WinPalm")
        .set("FileDescription", "WinPalm local server and Windows input engine")
        .set("CompanyName", "RS Application")
        .set("LegalCopyright", "Copyright RS Application. AGPL-3.0-only.")
        .set("OriginalFilename", "winpalm_core.dll")
        .set("FileVersion", &version)
        .set("ProductVersion", &version)
        .set_version_info(winresource::VersionInfo::FILETYPE, 2) // VFT_DLL
        .compile()
        .expect("Compile WinPalm's Windows version resource");
}
