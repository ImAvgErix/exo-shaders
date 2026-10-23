# Prebuilt native files

Taken unchanged from SCSKiller 1.4.0-alpha.3 (the alpha feed's `SCSKiller.App-1.4.0-alpha.3-alpha-full.nupkg`, SHA-256
efd603f60f06bf02e06f3f19013b38124268020f48464dd22b14fec660d30b75, `lib\app\`). That alpha's source isn't public, so this
branch stays local: it is never pushed or released until upstream publishes 1.4's source. `proxy\` holds the 1.3.x source
of the DX12 files only; the Vulkan layer, fossilize-replay and the preload proxies are new in 1.4. alpha.3 ships the same
files as alpha.2; the preload proxies are still alpha.2's, because alpha.3's differ from them only in their build
timestamps. publish-service.ps1 copies them into engine\out\service in the app's layout.

| File | SHA-256 | From |
| --- | --- | --- |
| native/VkLayer_scskiller.dll | 4d505d019f624ba0a5fb9cb3e5b0a25d6a56991e3f261eb7c68e31dd24e5379f | SCSKiller 1.4.0-alpha.3 |
| native/amd_ags_x64.dll | 482b241ea855fdd59aaa7687c385ce4165b5340857e6a0e6d15ba27ecb65ee70 | SCSKiller 1.4.0-alpha.3 |
| native/d3d12.dll | 4fb155e32eba2f3f505a125a5c660ee1160fee55504f342650cd9fe9f83f9398 | SCSKiller 1.4.0-alpha.3 |
| native/fossilize-replay.exe | 20abea030f76fab9f3cc0125f998079c89fa50108d0ce962abd1d33c9bbd5c63 | SCSKiller 1.4.0-alpha.3 |
| native/preload/dinput8.dll | b2d33a8796eb888598c5ac22c1afb3eab6dcf000c41120483814dddd423678ec | SCSKiller 1.4.0-alpha.2 |
| native/preload/version.dll | 48f3db506b0776cfa141bd817b29c1a427b79c69f3f7c2b61157fc5a4127ae18 | SCSKiller 1.4.0-alpha.2 |
| native/preload/winmm.dll | 168eeef7f1f8979adf3b9bd7709d7ae19db41e70e305e5c00c4b4052344d1476 | SCSKiller 1.4.0-alpha.2 |
| native/scskiller_ags.dll | ae473148b2a29abf314785bc3c77673e821d1ca089b319fbbc571e900aa61798 | SCSKiller 1.4.0-alpha.3 |
| native/scskiller_warm.exe | 5b29b79b7d564f66a39fbb991309e07e5334632c80a5b2ce1990bb4977f635e3 | SCSKiller 1.4.0-alpha.3 |
| native/segheap/scskiller_warm.exe | 8c6515363a260ebec4087a1447d88812e98c0569f2a8fefa367d7f3746cee7dc | SCSKiller 1.4.0-alpha.3 |
| zlib-ng2.dll | 454be2f3d10f804ace577198401431db5e95d0286b59589bc28a40085388e7c2 | SCSKiller 1.4.0-alpha.3 |
