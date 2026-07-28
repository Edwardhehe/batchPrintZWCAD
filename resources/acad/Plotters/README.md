AutoCAD 的 LA 系列统一使用此目录中的 PIA2 模板：

- `PIA2/LA_pdf.pc3`
- `PIA2/LA_png.pc3`
- `PIA2/LA_jpg.pc3`
- `PIA2/LA_dwf.pc3`
- `PIA2/PMP Files/` 下对应的 PMP

这些文件随构建复制到输出目录的 `Plotters/PIA2/`。安装时，不论 AutoCAD
版本及其自带 PC3 是 PIA2 还是 PIA3，都以这些已验证模板生成 `LA_*`。
`LA_pdf`、`LA_dwf` 各包含 85 个毫米纸张规格；`LA_png`、`LA_jpg`
各包含 170 个像素介质（85 个规格的横、竖方向）。安装时不读取、不转换、
不合并已有 LA PIA，直接以这些模板覆盖插件自有 LA 文件；运行中的任意尺寸
由打印流程在新 PMP 上重新注册。
用户的 `DWG To PDF`、`PublishToWeb`、`DWF6 ePlot` 等配置始终只读，
仅提取驱动路径等白名单元数据；生成过程只覆盖插件自有的 `LA_*`。

`PIA3/` 仅保留为历史格式样本，不参与 LA 配置生成或安装。
