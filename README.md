# TableEdit (macOS)

一个独立编写的 CSV / TSV 表格编辑器（Avalonia + .NET 8），功能参考 Ron's Data Edit，但不包含其任何代码或资源。

对照目标已确定为新版 **Ron’s Data Edit**，目前尚未达到 1:1。已有功能、确认的限制和待对照项目见 [操作对照与验收清单](docs/RONS_PARITY.md)。

## 功能
- 打开：自动识别分隔符（`,` Tab `;` `|`）、编码（UTF-8/BOM、UTF-16、Latin-1）、引号内换行；拖放文件、Finder“打开方式”
- 编辑单元格、插入/复制/删除行、从剪贴板粘贴行、插入/重命名/删除/移动列、首行作为表头开关
- 点击列头切换升序 / 降序；Column 菜单可以恢复原始顺序。数字、日期、文本分开比较（"Item 2" 排在 "Item 10" 前，`1,5` 这类欧式小数也认），空值永远排在最后；30 万行约 0.2–0.6 秒
- 搜索（⌘F）：输入即筛选；多个词用空格分开（"全部词"或"任一词"都可以选）；`"短语"` 按整句匹配；`*` 和 `?` 是通配符；"Not" 用来取反；默认忽略大小写和重音（cafe 能找到 Café）；可以只搜某一列；匹配的格子会高亮，Enter / ⌘G 跳到下一个，⇧⌘G 跳到上一个
- 自动填充列（Column › Auto-Fill Column…，⇧⌘F）：按模板生成数据，可以填到现有列，也可以新建一列，还能顺便在末尾加 N 行。可选择填全部行、只填空格、只填选中行，或只填搜索结果里的行；可以撤销
  - `%n7%` 7 位数字，`%c7%` / `%C7%` 7 个小写 / 大写字母，`%a7%` / `%A7%` 字母数字混合（数字可以写任意长度）
  - `%n18-65%` 指定范围内的整数，`%row%` 行号，`%row:1000%` 从 1000 开始编号，`%pick:A|B|C%` 从中随机选一个
  - `%fname%` `%lname%` `%name%` `%email%` `%phone%` `%city%` `%country%` `%company%` `%date%` `%uuid%` `%bool%`（同一行里的名字和邮箱对得上）
  - `%[列名]%` 引用同一行另一列的值，`%%` 表示一个 % 号；例如 `(%n3%) %n3%-%n4%`、`%[First Name]%.%[Last Name]%@example.com`
- 打开文件时自动识别分隔符（`,` `;` Tab `|` `:`，也支持 Excel 写入的 `sep=` 行），并自动判断有没有表头；没有表头会自动补上 Column 1、Column 2…，状态栏会显示识别结果。识别错了可以用 File › Reopen with Delimiter 和 Column › First Row Is Header 改
- 行数没有硬性上限，只受内存限制：30 万行×10 列大约占 500 MB
- 查找与替换（整格匹配、正则、仅当前列、计数）
- 数据清理：去空白、删空行、去重、向下填充、大小写转换
- 列统计：空值、唯一值、求和/平均/中位数/标准差/最值、高频值
- 导出（当前筛选结果）：JSON、HTML、XML、Markdown、SQL INSERT、TSV、CSV
- 撤销/重做（⌘Z / ⇧⌘Z），未保存时关闭会提示

## 在 Windows 上打包 macOS 版
```
python packaging/package_macos.py
```
输出 `dist/TableEdit-macos-arm64.zip`（Apple 芯片）和 `dist/TableEdit-macos-x64.zip`（Intel）。

## 在 Mac 上安装（1.3.3 起）
从 Releases 下载对应芯片的 **DMG**，打开后把 **TableEdit.app** 拖到 **Applications**，推出磁盘映像，再从 Applications 启动。应用在 macOS 构建机上提前签名，不再要求下载后运行终端补签名。

DMG 本身不能代替 Apple Developer 签名和公证。若该 Release 标注 **Ad-hoc signed; not Apple-notarized**，首次启动可能仍需到 **系统设置 › 隐私与安全性 › 仍要打开** 批准 TableEdit（macOS 12 在“系统偏好设置 › 安全性与隐私”）。后续同一份已安装应用通常无需重复批准，新下载的更新可能需要再次批准。

完整签名、公证配置和本地打包步骤见 [macOS 发布说明](packaging/MACOS_RELEASE.md)。

## 在 Mac 上直接编译
安装 .NET 8 SDK 后运行 `packaging/build-on-mac.sh`。

## 单元格选择、删除与重复检查（1.3.0）

- 单击选中一个单元格；按住鼠标拖动可框选连续的矩形区域。拖到表格边缘会自动滚动。
- 先单击起点，再按住 **Shift** 点击终点，选中两点之间的单元格；继续 Shift 点击会保留原起点。
- **Shift+方向键** 扩展选区；**Ctrl+Shift+↑/↓** 扩展到当前列的首行 / 末行。macOS 同时支持 Cmd。
- **Ctrl+Space** 或 **Select Column** 选中当前列的可见单元格；点击左侧行号或 **Select Rows** 选中整行，Shift 点击行号选择连续多行。
- 点击列标题切换 **A→Z / Z→A** 排序；选择整列请使用 Select Column 或 Ctrl+Space。
- **Delete / Backspace** 清空选中单元格；**Delete Rows**、右键菜单或 **Ctrl/Cmd+Delete/Backspace** 删除这些单元格所在的整行。以上操作都支持撤销。
- **Ctrl/Cmd+C / X / V** 复制、剪切、粘贴矩形单元格，使用制表符分隔列。单个值可以填满选区；超出表格大小的粘贴会提示先添加行或列。
- 双击、**F2 / Enter** 开始编辑单元格，也可以直接输入新值。编辑状态下保留文本框的选择和快捷键操作。
- **Find Column Duplicates** 检查当前列在整个文档中的重复值，显示每组的所有记录，包含首次出现的记录。比较区分大小写和空格，忽略空单元格；不会删除数据。删除 / 修改重复项后结果会刷新，**Show All** 清除筛选。

### Interaction checks

See [tests/README.md](tests/README.md) for native mouse/keyboard regression checks. These cover cell selection, drag/Shift ranges, deletion/undo, sorting, duplicate filtering, editing and edge scrolling.

## 选区内批量操作（1.3.1）

- **Find & Replace** 的 **Scope** 可选 **Visible cells**（可见单元格）、**Current column**（当前可见列）或 **Selected cells**（选中单元格）。
- 选择 **Selected cells** 时记录当前选区；连续查找、计数和替换都保留该范围。要换范围，在表格中重新选择后点击 **Use current selection**。
- 排序后仍追踪原来的记录；筛选隐藏的行不参与操作。切换文件或重建列后会要求重新选择，避免沿用旧范围。
- **Data › Selected Cells** 可对选区去空白、转换大小写或标题大小写；右键菜单也提供去空白和大小写转换。
- 批量替换与转换可以一次撤销/重做；清空或修改单元格后，当前搜索及重复筛选会刷新。

## 输入覆盖与重复值高亮（1.3.3）

- 选中一个或多个单元格后直接输入新内容，按 Enter 提交时会覆盖全部选中的格子；不会附加到旧内容后面。Esc 取消输入，一次撤销恢复所有格子各自原来的值。
- 选中整列后直接输入也会覆盖该列全部可见单元格。F2、Enter 或双击进入的普通编辑仍只处理当前格子。
- 列重复结果会把重复格子标黄；选中的重复格子使用紫色，未选列不变色。Show All 同时清除重复筛选和高亮。
- 修复清空搜索框后的延迟刷新清除刚建立的单元格选区的问题。
