# TableEdit (macOS)

一个独立编写的 CSV / TSV 表格编辑器（Avalonia + .NET 8），功能参考 Ron's Data Edit，但不包含其任何代码或资源。

## 功能
- 打开：自动识别分隔符（`,` Tab `;` `|`）、编码（UTF-8/BOM、UTF-16、Latin-1）、引号内换行；拖放文件、Finder“打开方式”
- 编辑单元格、插入/复制/删除行、从剪贴板粘贴行、插入/重命名/删除/移动列、首行作为表头开关
- 点击列头排序：↑ 升序 → ↓ 降序 → 恢复原始顺序。数字、日期、文本分开比较（"Item 2" 排在 "Item 10" 前，`1,5` 这类欧式小数也认），空值永远排在最后；30 万行约 0.2–0.6 秒
- 搜索（⌘F）：输入即筛选；多个词用空格分开（"全部词"或"任一词"都可以选）；`"短语"` 按整句匹配；`*` 和 `?` 是通配符；"Not" 用来取反；默认忽略大小写和重音（cafe 能找到 Café）；可以只搜某一列；匹配的格子会高亮，Enter / ⌘G 跳到下一个，⇧⌘G 跳到上一个
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
