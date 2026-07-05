@tool
extends EditorScript

# 要扫描的根目录（相对于项目根目录）
const SCAN_ROOT := "res://"
# 输出文件路径（相对于项目根目录）
const OUTPUT_FILE := "res://custom_strings.pot"

# 用于去重存储的字典
var _strings := {}

func _run() -> void:
	print("开始提取 tr() 字符串...")
	_strings.clear()
	_scan_directory(SCAN_ROOT)
	_save_to_pot(OUTPUT_FILE)
	print("提取完成，共找到 %d 个唯一字符串。" % _strings.size())

# 递归扫描目录
func _scan_directory(dir_path: String) -> void:
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return
	
	dir.list_dir_begin()
	var file_name := dir.get_next()
	while file_name != "":
		var full_path := dir_path.path_join(file_name)
		if dir.current_is_dir():
			# 跳过 .git、.godot 等隐藏目录
			if not file_name.begins_with("."):
				_scan_directory(full_path)
		else:
			# 只处理 .gd 和 .tscn 文件
			if file_name.ends_with(".gd") or file_name.ends_with(".tscn"):
				_extract_from_file(full_path)
		file_name = dir.get_next()
	dir.list_dir_end()

# 从单个文件中提取 tr() 字符串
func _extract_from_file(file_path: String) -> void:
	var file := FileAccess.open(file_path, FileAccess.READ)
	if file == null:
		return
	
	var content := file.get_as_text()
	file.close()
	
	# 根据文件扩展名选择正则表达式
	var regex: RegEx
	if file_path.ends_with(".gd"):
		regex = RegEx.new()
		# 匹配 tr("...") 或 tr('...')，允许空格
		regex.compile(r'tr\s*\(\s*(["\'])(.*?)\1\s*\)')
	elif file_path.ends_with(".tscn"):
		regex = RegEx.new()
		# 匹配 tr(\"...\") 或 tr(\'...\') （转义引号）
		regex.compile(r'tr\s*\(\s*\\"([^\\"]*)\\".*?\)|tr\s*\(\s*\\\'([^\\\']*)\\\'.*?\)')
	else:
		return
	
	var result := regex.search_all(content)
	for match in result:
		# 对于 .gd，捕获组 2 是字符串内容
		if file_path.ends_with(".gd"):
			var str_content := match.strings[2]
			_strings[str_content] = true
		# 对于 .tscn，捕获组 1 或 2 是内容（根据引号类型）
		elif file_path.ends_with(".tscn"):
			var str_content := ""
			if not match.strings[1].is_empty():
				str_content = match.strings[1]
			elif not match.strings[2].is_empty():
				str_content = match.strings[2]
			if not str_content.is_empty():
				_strings[str_content] = true

# 保存为 POT 格式文件
func _save_to_pot(output_path: String) -> void:
	var file := FileAccess.open(output_path, FileAccess.WRITE)
	if file == null:
		print("无法创建输出文件：", output_path)
		return
	
	# 写入 POT 头部
	file.store_string('msgid ""\n')
	file.store_string('msgstr ""\n')
	file.store_string('"Project-Id-Version: custom\\n"\n')
	file.store_string('"Content-Type: text/plain; charset=UTF-8\\n"\n')
	file.store_string('"Content-Transfer-Encoding: 8bit\\n"\n\n')
	
	# 写入每个字符串
	for msgid in _strings.keys():
		file.store_string('msgid "%s"\n' % _escape_pot(msgid))
		file.store_string('msgstr ""\n\n')
	
	file.close()
	print("POT 文件已保存至：", output_path)

# 对 POT 中的特殊字符进行转义（如双引号、换行等）
func _escape_pot(text: String) -> String:
	# 简单转义双引号和反斜杠
	text = text.replace("\\", "\\\\")
	text = text.replace("\"", "\\\"")
	# 如果需要，也可以处理换行符等
	return text
