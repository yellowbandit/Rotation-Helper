Stable DH5 backup — 2026-10-22
================================
Git tag   : stable-dh5-2026-10-22
Commit    : 5c7b95259b91349e5559ca68b1a8a227156a7b72
Source    : RotationHelper.DH5\RotationHelperPlugin.cs
Build     : dotnet build -c Release (net10.0-windows, x64), 0 errors / 4 pre-existing warnings
DLL       : RotationHelper.dll  166912 bytes  MD5 5322C58A331C8ED7AA2F3C4FA582A1AE
Deployed  : %LOCALAPPDATA%\DungeonHelperData\plugins\RotationHelper\RotationHelper.dll
SDK       : lib\VoK.Sdk.dll 5.0.0-alpha.581+15c62e7dedbf0013b713314c62ab41f6bf6733d3 (MD5 EE89829BE268A346D84F946665DD4365)

This is the state to restore to if the SDK 5.0.0 release bump / InvokeHotbar migration goes wrong.
Restore: copy RotationHelper.dll from this folder to the deployed path above, or `git checkout stable-dh5-2026-10-22 -- RotationHelper.DH5/`.
