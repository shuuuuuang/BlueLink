# Repository Guidelines

## Project Structure & Module Organization

- `windows/BlueLink.App/` is the .NET 8 WPF client; verification lives in `windows/BlueLink.TransferVerification/`.
- `installer/` contains the WiX bundle, MSI, setup UI, runtime launcher, and uninstaller.
- `android/app/` is the Kotlin/Compose app. Protocol logic and vectors live under `android/protocol-core/`, `protocol-core/`, `shared/`, and `shared-spec/`.
- Assets live in `design/`; entry points live in `scripts/`. `.build/`, `.acceptance/`, Gradle work directories, and `artifacts/` are generated.

## Build, Test, and Development Commands

Run from the repository root in PowerShell:

```powershell
.\gradlew.bat verify
.\scripts\build-android.ps1
.\scripts\build-windows.ps1
.\scripts\verify.ps1
.\scripts\build-windows-release.ps1 -Configuration Release
```

These verify protocols, test/lint/build Android, build/verify Windows, and produce the framework-dependent WiX release. Validate artifacts with `scripts/verify-release-artifacts.ps1`. Toolchains require JDK 17, Android SDK 34, and .NET 8.

## Coding Style & Testing

Use four-space indentation, `PascalCase` for types/public members, and `camelCase` for locals/parameters. Preserve local private-field conventions. Name Kotlin tests `*Test.kt`, acceptance scripts `test-*.ps1`, and static gates `verify-*.ps1`.

Use WPF-UI 4.3 official controls/styles; never add base-control templates or restore `Themes/Controls.xaml`. Cover behavior changes with regressions. UI acceptance requires real UI Automation and fresh screenshots.

## Dialog UI Rules（弹窗强制规范）

- 禁止使用、新增或恢复默认 MessageBox。适用于 Windows 客户端、启动器、安装器、卸载器的全部用户界面，包括信息、警告、错误、异常兜底和确认流程；不得直接展示 System.Windows.MessageBox、System.Windows.Forms.MessageBox 或 Wpf.Ui.Controls.MessageBox，也不得在错误处理时回退到默认弹窗。
- 必须复用已经统一并验收的项目弹窗组件：客户端使用 BlueLinkDialog / ConfirmationWindow；启动与卸载错误提示使用 InstallerNoticeWindow；安装与卸载确认由 InstallerConfirmationDialog / InstallerPromptWindow 调用 InstallerDialogWindow。新增场景应扩展这些共用组件，禁止在业务代码中另起一套弹窗样式。仅复用官方控件不代表默认弹窗外观可以接受。
- 客户端与安装模块的共用弹窗尺寸统一使用 shared/WindowsDialogLayout.cs。不得在 Loaded / ApplyTemplate 后修补默认弹窗视觉树；业务入口只提供内容与操作语义，不自行定义弹窗布局。静态检查必须覆盖 installer/SharedUI，且不能要求或放行旧的默认弹窗实现。
- 普通提示与危险操作必须保留各自正确的按钮语义和样式；不存在的操作必须折叠，禁止空白按钮、按钮无故拉伸、文字或图标裁剪。标题与关闭按钮、正文与图标须垂直对齐；长内容在限定区域滚动，并遵循所属模块的主题、字体及间距规范。
- 短暂操作反馈使用项目既有的自动消失消息提示组件，不能改用默认 MessageBox 或占位且常驻的提示条。
- 弹窗验证与演示必须实例化实际共用组件，不能用默认 MessageBox 代替。涉及弹窗修改时检查所有受影响入口，并验证按钮可见性、文字/图标对齐、长内容及所属模块支持的主题；仍须遵守上文禁止新增基础控件模板的约束。

## Agent Workflow

- Freeze scope as explicit files/components, behaviors, and verification items. Obtain approval before expanding it.
- For replacement plans, maintain a supersession checklist. Revert only obsolete current-task changes; preserve unrelated or pre-existing work. Compare the final diff with the latest scope.
- Use proportional verification. Reserve releases and installation matrices for cross-module, high-risk, release-gate, or requested work. Mark untested items **Not verified**.
- Commit and push only when explicitly requested. Keep Conventional Commits scoped; exclude generated output, signing material, logs, secrets, and user data.
- Update root `WORKLOG.md` in Chinese after completed tasks with effective changes, root cause, verification, risks, next steps, and decisions. Keep one evolving entry per task; remove obsolete details.
- Update root `PROJECT_ROADMAP.md` only when long-term direction, capability baselines, operating strategy, security posture, or material decision boundaries change. Replace outdated statements.
- Keep `docs/IMPLEMENTATION_STATUS.md` focused on verified capabilities, limitations, and release blockers. These files are canonical for task history, planning, and status; create no competing reports.
- Refactor nearby violations only within the accepted scope or after separate approval.

## Commit, Pull Request & Safety Guidelines

Use Conventional Commits, for example `fix(installer): handle repeat upgrades`. PRs must describe visible behavior, commands/results, relevant issues, and UI screenshots. Installer tests require validated isolated paths. Never delete `Download`, app data, or unrelated files unless an approved test requires it.
