# WinForms → WPF 转换规范（LA打印 插件）

所有窗体位于 `src/Common/Views/`。工程（BatchPlotter / AcadBatchPlot / AcadBatchPlot.Core）已启用 `UseWpf`，且 csproj 以通配符包含 `..\Common\**\*.cs`（Compile）和 `..\Common\**\*.xaml`（Page），**无需改 csproj**。

## 目标
- 界面布局与原 WinForms 窗体**基本一致**（同标题、同控件、同相对布局、同尺寸观感）。
- 交互逻辑**完全不变**：所有事件处理、业务流程、公开属性/方法保持原样。
- 仅改 UI 层；不得修改 Models/Services/Commands 的业务逻辑。

## 文件约定
- 每个窗体 `FooForm.cs` → 新建 `FooForm.xaml` + `FooForm.xaml.cs`，类名、命名空间（`ZwcadBatchPlot`）与原来完全一致。
- 完成后**删除**旧的 WinForms `.cs` 文件（`git rm` 或直接删除文件）。
- XAML 根元素用 `<Window>`（模式对话框）或 `<Window>`（非模态面板，BatchPlotForm/RectangleBatchPlotForm 也是 Window）。加：
  ```xml
  xmlns:local="clr-namespace:ZwcadBatchPlot"
  Style="{StaticResource WindowBaseStyle}"
  ```
- code-behind 里 `InitializeComponent()` + 原 ctor 逻辑照搬（改造为 WPF 语义）。
- Window 构造完成后不要访问 `Controls`、`Owner` 等 WinForms 属性。

## 尺寸规则
WPF 使用设备无关单位且自动 DPI 缩放，**原 `UiLayout.Scale(n)` 一律当作 n 直接使用**（即 96-DPI 设计值）。例如 `UiLayout.ConfigureForm(this, 660, 370, 620, 340)` → `Width="660" Height="370" MinWidth="620" MinHeight="340"`。可缩放窗体 `ResizeMode="CanResize"`，FixedDialog → `ResizeMode="NoResize" WindowStyle="ToolWindow" ShowInTaskbar="False"`（工具样式对话框保留 NoResize 即可，不必 ToolWindow）。非模态主面板用 `ResizeMode="CanResize"`。
`WindowStartupLocation="CenterScreen"`（原 CenterParent 的对话框统一用 CenterScreen，因为 CAD 宿主没有可靠的父句柄）。

## 控件映射
| WinForms | WPF |
|---|---|
| TableLayoutPanel | Grid（`<Grid.ColumnDefinitions>`/`RowDefinitions`；Absolute→固定值，Percent→`*`，AutoSize→`Auto`） |
| FlowLayoutPanel(横) | `StackPanel Horizontal`（不换行）或 `WrapPanel`（原 WrapContents=true） |
| FlowLayoutPanel(右对齐按钮行) | `DockPanel LastChildFill=False`（注意 WinForms RightToLeft 添加顺序是**先加的在最右**，按视觉顺序排列） |
| Dock=Fill | Grid 内 `Grid.Row/Column` + `Margin=0` 撑满 |
| Label | `TextBlock` 或 `Label`（用 `VerticalAlignment=Center`） |
| TextBox | `TextBox`（ReadOnly→IsReadOnly，多行→`AcceptsReturn=True TextWrapping=Wrap VerticalScrollBarVisibility=Auto`） |
| ComboBox DropDownList | `ComboBox` + `IsEditable=False`；SelectedIndexChanged→`SelectionChanged`；`Items.AddRange(strings)`→ code-behind `_combo.Items.Add(string)` 仍可用 |
| CheckBox | `CheckBox` Content=文本，Checked→`IsChecked`（注意 WPF IsChecked 是 `bool?`，逻辑判断用 `== true`） |
| RadioButton | `RadioButton` Content=文本，GroupName |
| NumericUpDown | `TextBox` + 原有最小/最大/小数位约束封装为辅助方法（沿用原校验逻辑与初始值） |
| Button | `Button`（`DialogResult.OK` → `IsDefault=True` + 点击处理器设置 `DialogResult=true`；`DialogResult.Cancel` → `IsCancel=True`） |
| DataGridView | `DataGrid`，`Style="{StaticResource PluginGridStyle}"`，列定义照搬（DataGridTextColumn + Binding 或 code-behind 动态建列），VirtualMode/手工数据维护逻辑照搬到 code-behind（通常直接 ItemsSource=List<T> 更简单，但**不得改变**行的增删改行为） |
| Timer | `System.Windows.Threading.DispatcherTimer`（Tick/Interval 语义一致） |
| PictureBox/Icon | 若原图标资源存在则 `Image`，否则省略（不新增缺失资源） |
| SplitContainer | Grid 两列/两行 + `GridSplitter` |
| TabControl | `TabControl`/`TabItem` Header=原标题 |
| GroupBox | `GroupBox` Header |
| ToolTip | `ToolTip` 属性 |
| ContextMenuStrip | `ContextMenu` + MenuItem |
| Drag/Drop | `AllowDrop` + Drop/DragOver（`DragDropEffects` 相同语义） |

## 常用 API 替换
- `MessageBox.Show(...)` → `System.Windows.MessageBox.Show(...)`：`(owner)` 参数一律去掉；`MessageBoxButtons.OK/OKCancel/YesNo/YesNoCancel` → `MessageBoxButton.*`；`MessageBoxIcon.*` → `MessageBoxImage.*`。返回值 `DialogResult.OK/Yes` → `MessageBoxResult.OK/Yes`。**文字与按钮语义保持原样**。
- `OpenFileDialog/SaveFileDialog` → `Microsoft.Win32.OpenFileDialog / SaveFileDialog`（Filter/AddExtension/OverwritePrompt/FileName 等同名属性基本一致）。`FolderBrowserDialog` 保留 WinForms 版（WPF 没有等价物），但 `ShowDialog(IWin32Window)` → `ShowDialog()`，返回值 `DialogResult.OK` → `true`。
- `Color.FromArgb / System.Drawing` 尺寸 → WPF `System.Windows.Media.Color` / `Thickness`。
- `Cursor.Position` → `System.Windows.Forms.Cursor.Position` 仍可用；`Screen.FromPoint(Cursor.Position).WorkingArea` → `SystemParameters.WorkArea`。
- `UiLayout.CreateButton(text, minW)` → XAML `<Button Content="..." MinWidth=".." Style="{StaticResource PluginButtonStyle}"/>`（MinWidth 用原最小宽度值）。
- `UiLayout.AddRow(table, row, label, control)` → XAML Grid 两列行。
- `UiLayout.Clamp(numericUpDown, v)`/`InitMarginCombo`/`ReadMarginValue` 等辅助逻辑 → 在窗体内实现同样语义的私有方法（保持数值范围/格式化输出一致）。
- `CadWindowFocus.HideForCadInput(form)` → `CadWindowFocus.HideForCadInput(this)`（已提供 Window 版）；`RestoreDialog` 同理。
- `form.Close()/Show()/Hide()/Activate()/TopMost/Opacity` → WPF Window 同名成员；`FormClosing(e.Cancel=...)` → `Closing((s,e)=> e.Cancel=...)`；`FormClosed` → `Closed`。
- `Text = "标题"` → XAML `Title`。`Icon` 保留原逻辑（如原窗体没设 Icon 就不设）。
- `Visible` / `IsDisposed` → `IsVisible` / `IsLoaded`（调用方由主线处理，窗体内部同理）。
- `SuspendLayout/ResumeLayout/PerformLayout` → 直接删除。
- WPF 模态显示：**code-behind 内部不要调用 `ShowDialog()`/`Show()` 自身**；由调用方负责（主线改造）。窗体内部触发的子对话框（如 `new XxxForm(...).ShowDialog()`）改为 `CadDialog.ShowModal(dialog)`，返回值 `DialogResult.OK` → `== true`。
- 窗体内部 `ShowDialog(this)`（文件对话框等）→ `ShowDialog()` 或传 `this`（Microsoft.Win32 版接受 Window owner）。
- `DoubleBuffered` 反射、`Application.DoEvents`（WinForms）→ 删除；需要消息泵时用 `Dispatcher.Invoke(DispatcherPriority.Background, ...)`。
- `Clipboard.SetText/GetText` → `System.Windows.Clipboard`。
- `Font`/`TextRenderer.MeasureText` → 删除（WPF 布局自适应）；按钮宽度交给 MinWidth/Auto。

## 交互逻辑红线
1. 所有 public/internal 属性、方法、事件签名**不得改名改义**（调用方遍布 Commands/Services，主线会统一适配 `using var`→普通变量、`DialogResult`→`bool?`，但属性名必须保持）。
2. 事件处理里的业务调用（服务方法、CAD API、打印流程）**逐行保留**。
3. 快捷键（AcceptButton/CancelButton/Ctrl 组合键）必须以 `IsDefault`/`IsCancel`/`KeyBinding`/`PreviewKeyDown` 等价保留。
4. 默认值、初始选中项、控件启用状态、校验提示文字逐项保留。
5. 若原窗体有 `ReadOnly` 属性对外暴露状态（如 SelectedPaper、HasPendingPrint），保持相同语义。

## 完成自检
- grep 确认新 `.xaml.cs` 无 `System.Windows.Forms` 引用（FolderBrowserDialog/FileDialog 允许的除外，需注明）。
- 删除了对应的旧 WinForms `.cs` 文件。
- XAML 中所有事件处理器在 code-behind 中存在。
