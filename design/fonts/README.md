# Noto Sans SC

Android 首批组件使用 Figma Foundations 指定的 Noto Sans SC，按 400/700 权重读取同一可变字体。

- 上游：Google Fonts，https://github.com/google/fonts/tree/main/ofl/notosanssc
- 原始文件：https://raw.githubusercontent.com/google/fonts/main/ofl/notosanssc/NotoSansSC%5Bwght%5D.ttf
- 本地字体：android/app/src/main/res/font/noto_sans_sc.ttf
- 许可证：本目录 NotoSansSC-OFL.txt（SIL Open Font License 1.1）
- 2026-09-05 下载原文件，没有重新绘制字形或修改字体。

Windows B2 首页使用同一来源，通过 fontTools 4.59.2 `varLib.instancer` 按 400/500/700 生成静态 TTF，以兼容 WPF 的字重选择。字体族保持 Noto Sans SC，文件位于 `windows/BlueLink.App/Assets/Fonts`；来源、字重和 SHA-256 见 `windows-noto-static.json`。Windows 输出与发布目录随附 OFL.txt，未重绘字形。
