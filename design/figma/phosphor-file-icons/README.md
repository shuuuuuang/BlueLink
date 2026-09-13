# Phosphor 文件图标候选

当前推荐预览为 v2，共 19 类。Word、表格、演示文稿采用衬线 W/S/P，字体采用衬线 Aa，其余类别使用图形识别。
Figma：https://www.figma.com/design/Tna537kDQdpsVwDX0lXlCs/BlueLink?node-id=1375-291

保留用户认可的 file-text、file-image、file-archive、file-code。PDF、DOC/DOCX、SQL 及配置文件独立匹配；其余同类扩展名共用图形。

代码映射已明确：java、py、go、js、ts、tsx、vue 以及常见 C/C++、C#、Kotlin、Swift、Rust、PHP、HTML/CSS 和脚本共用 file-code；JSON/XML 继续共用。49 个小写扩展名见 preview.json 的 extensionGroups.file-code；匹配不区分大小写，SQL 与配置文件的 exactFormatPriority 优先，未知文件兜底。客户端已通过 shared/file-icons/catalog.json 接入这些规则。

新增五类，预览行 1418:291，实际尺寸行 1418:292。每类均包含单色、浅色、深色及 20/24/40px 六个样本。电子书按用户要求不新增。
- 字体 file-font：自绘衬线 Aa，紫色 #7152BB / #BEA5F2；ttf、otf、woff、woff2、ttc。
- 磁盘镜像 file-disk-image：刻面光盘与中空盘芯，靛蓝 #4868AF / #9EBCFF；iso、img、vhd、vhdx、vmdk、qcow、qcow2、vdi、ova、ovf。系统镜像、虚拟机磁盘和虚拟设备模板统一使用此图标。
- 设计源文件 file-design-source：放大并舒展的斜向钢笔尖，扩大孔洞留白，玫紫 #B04479 / #F4A3CB；psd、psb、ai、sketch、fig、xd。
- 3D 与 CAD file-3d：等轴立方体，三个可见面以浅填色区分，青色 #07888B / #6DDCE0；blend、fbx、obj、stl、gltf、glb、3mf、step、stp、dwg、dxf。
- 证书 file-certificate：带勾印章，金色 #B47B12 / #F1CC75；crt、cer、pem、pfx、p12、p7b。

五类共 38 个扩展名已写入 extensionGroups；没有与现有代码/专用规则冲突。图形前景伸出纸张左下轮廓；Aa 使用自绘路径，其余最初参照 Phosphor disc、pen-nib、cube-transparent、seal-check 重绘；镜像与证书保留 26 × 0.62 = 16.12 的前景描边。设计源文件改为直接在 256 viewBox 绘制的舒展笔尖，外框 16、孔洞 14；3D/CAD 改为外框 16 的等轴立方体，去除交叉背线并增大主体。两枚图标的十二个样本已同步。来源路径和 SHA 见 additionalCategoriesRevision.sources；不依赖字体或外部图片。

Word 使用蓝色衬线 W（#176BFF / #87B6FF），表格使用绿色 S（#16804A / #71DBA1），演示文稿使用橙色 P（#C25E12 / #FFB76A）。三种字形由 BlueLink 自绘，包含粗细对比、衬线及弧形收笔，主体伸出纸张左下方，搭配正文横线。PDF 为自绘曲线，SQL 使用官方 database 前景。

配置文件 file-config 使用 Phosphor gear-six 与纸张错位组合，蓝灰色 #536D91 / #AAC2E0。conf、config、yaml、yml、properties、ini 优先映射到此类别。预览行 1403:291 含单色/浅深色与 20/24/40px 六个样本。

音频使用 BlueLink 自绘飘带单音符，弧形旗尾与倾斜中空符头；视频使用附件样式的四孔胶片卷盘。分别沿用紫色和玫红色。六个颜色/尺寸样本已同步，媒体实际尺寸对照位于 1406:291；前景笔画保持 16.12，替代首轮过细的 9.92。

用户微调后的 Figma 主图为当前图形依据。SQL、音频、配置、磁盘镜像、设计源文件、证书六类的 20/24/40px 共 18 个尺寸样本，直接克隆当前浅色主图并等比缩放；57 个主预览样本保持原样。同步验证比例、节点尺寸、位置、描边、颜色及边界，无差异或裁剪。六个 SVG 从主图重新导出，保留内部裁剪与相对位置，以 scale(6.4) 归一化到 256 viewBox；仅把主色替换为 currentColor。来源节点与替代样本台账见 userAdjustedSizeSync。

SVG 使用 currentColor，颜色、扩展名映射与节点台账见 pictogram-v2/preview.json。新增五类 30 个样本已完成截图和边界核对，五个 SVG 可解析、无 text 或外部引用。全板 19 卡片、1288×1794、408 个矢量节点，无缺失字体、遗留占位、画板越界或相邻画板重叠。已接入 Windows 与 Android；运行资源及完整分类表见 shared/file-icons，源 SVG 继续保留。

客户端共 177 个扩展名规则，已补入同类的 BMP/HEIC/AVIF、OGG/OPUS、TGZ/XZ、LOG、MSIX 等，复合压缩后缀以最后一段识别。其他数据库、TOML/CFG/ENV、Dockerfile 等未列入本轮规则，不另设新类别。

官方来源版本：2b75f3ad12b420c9504ef05df8d2564a28f8500e；许可证见 LICENSE。
根目录 svg/、manifest.json、figma-preview.json 和旧 ZIP 为初次 24 枚提取记录，仅保留历史对照；当前 SVG、来源、颜色、节点与修订台账位于 pictogram-v2/。

双端接入采用当前主图直接导出的 256×256 透明 PNG 共用遮罩及浅深色着色，来源与 SHA 见 shared/file-icons/export-manifest.json。Windows 原生渲染和 Android 构建/JVM 检查已通过；Android 真机浅深色 19 类图标及 20/24/40dp 已补验通过，证据 .acceptance/file-icons-android-device。
