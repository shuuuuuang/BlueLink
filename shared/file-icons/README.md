# 双端文件类型图标

当前 19 类来自用户确认并微调后的 Figma 主图：[预览板 1375:291](https://www.figma.com/design/Tna537kDQdpsVwDX0lXlCs/BlueLink?node-id=1375-291)。

- `catalog.json` 是 Windows / Android 共用的扩展名、MIME 回退和浅深色配置，共 177 个扩展名。扩展名优先且忽略大小写，仅解析文件名末段，不访问文件系统；无匹配时按 MIME 回退，最后使用未知文件图标。
- `res/drawable-nodpi` 是 19 个原稿直接以 6.4 倍导出的 256 × 256 透明 PNG。两端使用同一份资源作为透明度遮罩，保留双色层次，用各类别的主题色着色，不依赖平台字体或 SVG 运行库。常用显示尺寸为 20 / 24 / 40，Android 传输卡片为 46 dp。
- `export-manifest.json` 记录主图节点、资源 SHA-256 和可见边界。SVG 原稿仍保存在 `design/figma/phosphor-file-icons/pictogram-v2`。
- `assets/licenses/PhosphorIcons.txt` 随两端应用打入包，并出现在开源许可页面。
- `tests/file-icon-cases.tsv` 是两端共用的分类回归用例，包含专用类别优先级、代码文件、配置文件、qcow2 / ova / ovf、大小写、MIME 和未知类型。

修改分类或配色后运行：

```powershell
python scripts/generate-file-icons.py
python scripts/generate-file-icons.py --check
```

脚本生成 Windows 分类器、DrawingImage 资源及主题色，和 Android 分类器及资源映射。更新图形时先从 Figma 当前主图重新导出，保留 40 × 40 画布及内部透明边距，并更新导出台账。

验收入口：

- Windows：`BlueLink.TransferVerification.dll --file-icons=.acceptance/file-icons-windows`，原生后台窗口、真实附件模板/文件表格、UI Automation、主题切换和截图。
- Android：`:app:testDebugUnitTest :app:lintDebug :app:assembleDebug`。
- Android 真机：debug 专用 `AcceptanceActivity` 的 `scene=file-icons`，可用 `theme=light/dark`；只渲染生产图标组件和内存样例，窗口保持常亮，不写入聊天或传输记录。
