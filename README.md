# TableEdit (macOS)

一个独立编写的 CSV / TSV 表格编辑器（Avalonia + .NET 8），功能参考 Ron's Data Edit，但不包含其任何代码或资源。

## 功能
- 打开：自动识别分隔符（`,` Tab `;` `|`）、编码（UTF-8/BOM、UTF-16、Latin-1）、引号内换行；拖放文件、Finder“打开方式”
- 编辑单元格、插入/复制/删除行、从剪贴板粘贴行、插入/重命名/删除/移动列、首行作为表头开关
- 在 Column 菜单中选择升序、降序或恢复原始顺序。数字、日期、文本分开比较（"Item 2" 排在 "Item 10" 前，`1,5` 这类欧式小数也认），空值永远排在最后；30 万行约 0.2–0.6 秒
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

## 在 Mac 上首次运行
解压后双击 `first-run.command`（或在终端执行）：
```
xattr -cr TableEdit.app && codesign --force --deep --sign - TableEdit.app
```
在 Windows 上交叉编译出的程序没有签名，Apple 芯片的 Mac 会拒绝运行未签名的程序，所以必须先做一次本地 ad-hoc 签名。

## 在 Mac 上直接编译
安装 .NET 8 SDK 后运行 `packaging/build-on-mac.sh`。

## Row and column selection

- Click a row number or use **Select Row** / **Shift+Space** to select one row. Ctrl/Cmd-click adds rows; Shift-click selects a range.
- Click a column header or use **Select Column** / **Ctrl+Space** to select that column in the visible rows. Sorting is available in the **Column** menu.
- Select a cell, then press **Ctrl+Shift+Down** to select its column from that row to the last visible row. **Ctrl+Shift+Up** selects through the first visible row. These shortcuts also accept Cmd on macOS. While editing text, the editor keeps its normal keyboard behavior.
- **Ctrl/Cmd+C** copies only the selected column cells, in their displayed order. **Escape** returns to row selection.
- Select a row and click **Find Row Duplicates** (also in Edit and Data). It shows the original plus rows with exactly the same values in every column, including case and whitespace. It searches the whole document and does not delete or edit rows. **Show All** clears duplicate and search filters.
