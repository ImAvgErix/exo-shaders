# Prebuilt native files

Taken unchanged from SCSKiller 1.3.0 (the official SCSKiller-1.3.0-Portable.zip of the v1.3.0 GitHub release,
`current\`). Their source is in this repository (`proxy\`, built by `build\publish.ps1` with CMake); the release copies
are used so the files match what SCSKiller ships. publish-service.ps1 copies them into engine\out\service in the app's
layout.

| File | SHA-256 | From |
| --- | --- | --- |
| native\d3d12.dll | 1a66c0ee35e98958a4f667dffb1ad2a1d24b7d20938784b9bcb98ad332542162 | SCSKiller 1.3.0 |
| native\amd_ags_x64.dll | 482b241ea855fdd59aaa7687c385ce4165b5340857e6a0e6d15ba27ecb65ee70 | SCSKiller 1.3.0 (AMD AGS, signed by AMD; same as the betas) |
| native\scskiller_ags.dll | 4f3badbc9f17e414a21d675fcbe303e5eb5c535a823204acee8fd430154aff8b | SCSKiller 1.3.0 |
| native\scskiller_warm.exe | db3ea35070e883e2a7f85e31a4144a14059e611c3ad0f3ccaa41316f3dc11791 | SCSKiller 1.3.0 |
| native\segheap\scskiller_warm.exe | 8a26fbe6975de0614336715aae397295c8da86632cd782b57d02c3c3fbb21d91 | SCSKiller 1.3.0 |
| zlib-ng2.dll | 454be2f3d10f804ace577198401431db5e95d0286b59589bc28a40085388e7c2 | SCSKiller 1.3.0 (same as the betas) |
