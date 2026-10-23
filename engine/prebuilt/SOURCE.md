# Prebuilt native files

Taken unchanged from SCSKiller 1.4.0-alpha.2 (the signed alpha feed's `SCSKiller.App-1.4.0-alpha.2-alpha-full.nupkg`,
`lib\app\`; its SHA-256 matched the signed feed). That alpha's source isn't public, so this branch stays local: it is
never pushed or released until upstream publishes 1.4's source. `proxy\` holds the 1.3.x source of the DX12 files only;
the Vulkan layer, fossilize-replay and the preload proxies are new in 1.4. publish-service.ps1 copies them into
engine\out\service in the app's layout.

| File | SHA-256 | From |
| --- | --- | --- |
| native/VkLayer_scskiller.dll | 601d6af86d4ce8668443bf85722d1962c39b90e127e08883b64cb2fe4c65fbf0 | SCSKiller 1.4.0-alpha.2 |
| native/amd_ags_x64.dll | 482b241ea855fdd59aaa7687c385ce4165b5340857e6a0e6d15ba27ecb65ee70 | SCSKiller 1.4.0-alpha.2 |
| native/d3d12.dll | 927720cb6370d571c63bd9a782371fbaa18206845a40e77793b87906d25e2e0b | SCSKiller 1.4.0-alpha.2 |
| native/fossilize-replay.exe | 506cee9fe91653ad42bd3933bc1e595273a3b54e6c546da7442de3d1a05b69ca | SCSKiller 1.4.0-alpha.2 |
| native/preload/dinput8.dll | b2d33a8796eb888598c5ac22c1afb3eab6dcf000c41120483814dddd423678ec | SCSKiller 1.4.0-alpha.2 |
| native/preload/version.dll | 48f3db506b0776cfa141bd817b29c1a427b79c69f3f7c2b61157fc5a4127ae18 | SCSKiller 1.4.0-alpha.2 |
| native/preload/winmm.dll | 168eeef7f1f8979adf3b9bd7709d7ae19db41e70e305e5c00c4b4052344d1476 | SCSKiller 1.4.0-alpha.2 |
| native/scskiller_ags.dll | e588520cb66dbf528ac0335d26894dedf3e2ee8133cc47e26a4853f9c42ac760 | SCSKiller 1.4.0-alpha.2 |
| native/scskiller_warm.exe | 34828cb8ccfbfc696685d1c134c14cb4131a6ae7f93700c4d3a5eaa8f284e5fc | SCSKiller 1.4.0-alpha.2 |
| native/segheap/scskiller_warm.exe | 524f43ba063492e58c74fac266abbd66c2da0f1ed38924947e060b6bdb0cda38 | SCSKiller 1.4.0-alpha.2 |
| zlib-ng2.dll | 454be2f3d10f804ace577198401431db5e95d0286b59589bc28a40085388e7c2 | SCSKiller 1.4.0-alpha.2 |
