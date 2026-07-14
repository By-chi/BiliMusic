extends Node


func traverse_iterative(root: Node):
	var stack = [root]
	while stack.size() > 0:
		var node = stack.pop_back()  # 取出最后一个
		var tr_name := tr(node.name)
		if node.name != tr_name:
			node.name = tr_name
		# 将子节点压入栈（顺序无所谓）
		for child in node.get_children():
			stack.append(child)


# 生成特殊字符到 ASCII 的映射表
func _generate_unicode_map():
	var map := {}

	var ranges := [
		{"unicode_start": 0x1D400, "ascii_start": 0x41, "count": 26, "is_lower": false},
		{"unicode_start": 0x1D41A, "ascii_start": 0x61, "count": 26, "is_lower": true},
		{"unicode_start": 0x1D7CE, "ascii_start": 0x30, "count": 10, "is_digit": true},
		{"unicode_start": 0x1D434, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x1D44E, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0x1D468, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x1D482, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0x1D538, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x1D552, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0x1D7D8, "ascii_start": 0x30, "count": 10},
		{"unicode_start": 0x1D49C, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x1D4B6, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0x1D4D0, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x1D4EA, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0x1D504, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x1D51E, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0x1D5A0, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x1D5BA, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0x1D7E2, "ascii_start": 0x30, "count": 10},
		{"unicode_start": 0x1D5D4, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x1D5EE, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0x1D7EC, "ascii_start": 0x30, "count": 10},
		{"unicode_start": 0xFF21, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0xFF41, "ascii_start": 0x61, "count": 26},
		{"unicode_start": 0xFF10, "ascii_start": 0x30, "count": 10},
		{"unicode_start": 0x24B6, "ascii_start": 0x41, "count": 26},
		{"unicode_start": 0x24D0, "ascii_start": 0x61, "count": 26},
	]

	for r in ranges:
		for i in range(r.count):
			var unicode_char := char_from_code(r.unicode_start + i)
			var ascii_char := char_from_code(r.ascii_start + i)
			map[unicode_char] = (
				ascii_char.to_lower() if "is_lower" in r and r.is_lower else ascii_char.to_lower()
			)

	# 带圈数字 ①-⑨
	for i in range(1, 10):
		var unicode_char = char_from_code(0x2460 + i - 1)
		map[unicode_char] = str(i)
	map["⓪"] = "0"

	# 上标字母
	var superscript_letters = {
		"ᵃ": "a",
		"ᵇ": "b",
		"ᶜ": "c",
		"ᵈ": "d",
		"ᵉ": "e",
		"ᶠ": "f",
		"ᵍ": "g",
		"ʰ": "h",
		"ⁱ": "i",
		"ʲ": "j",
		"ᵏ": "k",
		"ˡ": "l",
		"ᵐ": "m",
		"ⁿ": "n",
		"ᵒ": "o",
		"ᵖ": "p",
		"ʳ": "r",
		"ˢ": "s",
		"ᵗ": "t",
		"ᵘ": "u",
		"ᵛ": "v",
		"ʷ": "w",
		"ˣ": "x",
		"ʸ": "y",
		"ᶻ": "z"
	}
	for ch in superscript_letters:
		map[ch] = superscript_letters[ch]

	# 下标字母
	var subscript_letters = {
		"ₐ": "a",
		"ₑ": "e",
		"ₕ": "h",
		"ᵢ": "i",
		"ⱼ": "j",
		"ₖ": "k",
		"ₗ": "l",
		"ₘ": "m",
		"ₙ": "n",
		"ₒ": "o",
		"ₚ": "p",
		"ᵣ": "r",
		"ₛ": "s",
		"ₜ": "t",
		"ᵤ": "u",
		"ᵥ": "v",
		"ₓ": "x"
	}
	for ch in subscript_letters:
		map[ch] = subscript_letters[ch]

	unicode_map = map


# 将 Unicode 码点转为字符
static func char_from_code(code: int) -> String:
	return String.chr(code)


var unicode_map := {}


# 在 RichTextLabel 中高亮匹配 a 的子序列
func apply_highlight(rich_label: RichTextLabel, a: String, b: String) -> void:
	rich_label.clear()

	var pattern := []
	for ch in a:
		var n = _normalize_char(ch)
		if n != "":
			pattern.append(n)

	if pattern.is_empty():
		rich_label.append_text(b)
		return

	var chars_original := []
	var chars_norm := []
	for ch in b:
		chars_original.append(ch)
		chars_norm.append(_normalize_char(ch))

	var match_indices := []
	var p_idx = 0
	for i in range(chars_norm.size()):
		if p_idx >= pattern.size():
			break
		if chars_norm[i] == pattern[p_idx]:
			match_indices.append(i)
			p_idx += 1

	var fully_matched: bool = p_idx == pattern.size()

	if not fully_matched:
		rich_label.append_text(b)
		return

	var next_match_idx = 0
	for i in range(chars_original.size()):
		if next_match_idx < match_indices.size() and i == match_indices[next_match_idx]:
			rich_label.push_color(Color("#f25d8e"))
			rich_label.append_text(chars_original[i])
			rich_label.pop()
			next_match_idx += 1
		else:
			rich_label.append_text(chars_original[i])


# 归一化字符：转小写并过滤无关字符
func _normalize_char(ch: String) -> String:
	if unicode_map.has(ch):
		return unicode_map[ch]

	var lower = ch.to_lower()
	if (lower >= "a" and lower <= "z") or (ch >= "0" and ch <= "9"):
		return lower if (lower >= "a" and lower <= "z") else ch

	return ""


# 配置文件管理
const CFG_PATH = "user://config.cfg"
signal saved
var _config_file = ConfigFile.new()
var _dirty = false
var _save_timer: SceneTreeTimer = null


func get_data(section: String, key: String, default = null):
	if _config_file.has_section(section) and _config_file.has_section_key(section, key):
		return _config_file.get_value(section, key)
	return default


func get_keys(section: String) -> Array:
	if _config_file.has_section(section):
		return _config_file.get_section_keys(section)
	return []


func set_data(section: String, key: String, value, immediately := false) -> void:
	_config_file.set_value(section, key, value)
	_dirty = true
	if immediately:
		flush()
	else:
		_schedule_save()


func remove_key(section: String, key: String) -> void:
	if _config_file.has_section_key(section, key):
		_config_file.erase_section_key(section, key)
		_dirty = true
		_schedule_save()


func set_key(section: String, old_key: String, new_key: String) -> Error:
	if _config_file.has_section_key(section, new_key):
		return ERR_ALREADY_EXISTS
	if _config_file.has_section_key(section, old_key):
		var value = _config_file.get_value(section, old_key)
		_config_file.erase_section_key(section, old_key)
		_config_file.set_value(section, new_key, value)
		_dirty = true
		_schedule_save()
		return OK
	return ERR_DOES_NOT_EXIST


# 延迟保存，避免频繁 IO
func _schedule_save():
	if _save_timer != null and _save_timer.time_left > 0:
		return
	_save_timer = get_tree().create_timer(2)
	_save_timer.timeout.connect(_do_save)


func _do_save():
	if _dirty:
		_config_file.save(CFG_PATH)
		_dirty = false
	_save_timer = null
	saved.emit()


func flush():
	# 取消已有的延迟保存计时器，避免重复保存
	if _save_timer != null:
		if _save_timer.timeout.is_connected(_do_save):
			_save_timer.timeout.disconnect(_do_save)
		_save_timer = null
	_do_save()


# 入口函数：在指定坐标显示右键菜单,右键菜单应用不了皮肤(即使我已经写了适配代码,还是不行)
func open_right_click_menu_window(pos: Vector2, data: Array[Dictionary]) -> void:
	var menu := PopupMenu.new()
	menu.name = "ContextMenu"
	add_child(menu)  # 将菜单添加到当前节点下，以便显示
	# 递归构建菜单内容
	_build_menu_from_data(menu, data)
	apply_theme_and_styles_to_node(menu)
	# 弹出菜单并设置位置
	menu.position = pos
	menu.popup()

	# 菜单关闭后自动销毁
	menu.popup_hide.connect(menu.queue_free)
	menu.notification(Control.NOTIFICATION_THEME_CHANGED)


func open_metadata_manager_window(directory_name: String, link: String) -> void:
	var metadata_manager_window: Window = preload("res://Scene/MetadataManager.tscn").instantiate()
	add_child(metadata_manager_window)
	apply_theme_and_styles_to_node(metadata_manager_window)
	metadata_manager_window.update(directory_name, link)


# 递归构建菜单项
func _build_menu_from_data(menu: PopupMenu, data: Array[Dictionary]) -> void:
	for item_dict in data:
		var visible = item_dict.get("visible", true)
		if not visible:
			continue

		var type = item_dict.get("type", "normal")
		var label = item_dict.get("label", "")
		var enabled = item_dict.get("enabled", true)
		var icon = item_dict.get("icon", null)
		var shortcut = item_dict.get("shortcut", "")
		var tooltip = item_dict.get("tooltip", "")
		var checked = item_dict.get("checked", false)
		var children = item_dict.get("children", [])

		# 拼接快捷键到标签(用 \t 分隔，显示时快捷键会靠右)
		var display_label = label
		if shortcut != "":
			if label != "":
				display_label = label + "\t" + shortcut
			else:
				display_label = "\t" + shortcut

		match type:
			"separator":
				if label != "":
					menu.add_separator(display_label)
				else:
					menu.add_separator()
				continue

			"submenu":
				var sub_menu := PopupMenu.new()
				sub_menu.name = "SubMenu_%d" % menu.get_child_count()
				menu.add_child(sub_menu)
				apply_theme_and_styles_to_node(sub_menu)
				_build_menu_from_data(sub_menu, children)

				# 添加子菜单项，不接收返回值
				menu.add_submenu_node_item(display_label, sub_menu)
				var idx = menu.item_count - 1

				_apply_common_item_props(menu, idx, enabled, icon, tooltip, item_dict)
				continue

			"checkbox":
				menu.add_check_item(display_label)
				var idx = menu.item_count - 1
				menu.set_item_checked(idx, checked)
				_apply_common_item_props(menu, idx, enabled, icon, tooltip, item_dict)

			"radio":
				menu.add_radio_check_item(display_label)
				var idx = menu.item_count - 1
				menu.set_item_checked(idx, checked)
				_apply_common_item_props(menu, idx, enabled, icon, tooltip, item_dict)

			_:  # "normal" 或其他
				menu.add_item(display_label)
				var idx = menu.item_count - 1
				_apply_common_item_props(menu, idx, enabled, icon, tooltip, item_dict)

	# 连接信号(只连接一次)
	if not menu.id_pressed.is_connected(_on_menu_item_clicked):
		menu.id_pressed.connect(_on_menu_item_clicked.bind(menu))


# 辅助函数：为普通 / checkbox / radio 项设置通用属性
func _apply_common_item_props(
	menu: PopupMenu, idx: int, enabled: bool, icon, tooltip: String, item_dict: Dictionary
) -> void:
	menu.set_item_disabled(idx, not enabled)
	if icon:
		_set_item_icon(menu, idx, icon)
	if tooltip != "":
		menu.set_item_tooltip(idx, tooltip)
	menu.set_item_metadata(idx, item_dict)


# 处理菜单项点击
func _on_menu_item_clicked(id: int, menu: PopupMenu) -> void:
	var item_dict = menu.get_item_metadata(id)
	if not item_dict:
		return

	# 执行 action
	if item_dict.has("action"):
		var action = item_dict["action"]
		if action is Callable:
			action.call(item_dict)  # 传入整个字典，可从中读取 data 等自定义信息
		elif action is String and has_method(action):
			call(action, item_dict)


# 辅助方法：设置图标(支持 Texture2D 对象或资源路径字符串)
func _set_item_icon(menu: PopupMenu, idx: int, icon) -> void:
	var texture: Texture2D
	if icon is Texture2D:
		texture = icon
	elif icon is String:
		texture = load(icon) as Texture2D
	if texture:
		menu.set_item_icon(idx, texture)


# 打开收藏夹选择窗口(选择模式)
func open_select_favorites_window() -> Array:
	var window = Window.new()
	add_child(window)
	var favorites_page = preload("res://Scene/Favorites.tscn").instantiate()
	favorites_page.offset_left = 10
	window.add_child(favorites_page)
	apply_theme_and_styles_to_node(window)
	window.size = Vector2i(1290, 901)
	window.min_size = Vector2(800, 600)
	window.popup_centered()
	window.title = tr("请选择收藏夹")
	window.transient = true
	window.exclusive = true
	window.close_requested.connect(func(): window.queue_free())
	favorites_page.set_select_mode(true)

	return await favorites_page.favorites_selected


# 打开普通收藏夹管理窗口(普通模式)
func open_normal_favorites_window() -> bool:
	var window = Window.new()
	add_child(window)
	var favorites_page = preload("res://Scene/Favorites.tscn").instantiate()
	favorites_page.offset_left = 10
	window.add_child(favorites_page)
	apply_theme_and_styles_to_node(window)
	window.size = Vector2i(1290, 901)
	window.min_size = Vector2(800, 600)
	window.popup_centered()
	window.title = "收藏夹"
	window.transient = true
	window.exclusive = true
	window.close_requested.connect(func(): window.queue_free())

	# 确保普通模式下选择模式关闭(默认即为 false，可省略但建议显式调用)
	favorites_page.set_select_mode(false)

	await favorites_page.tree_exiting
	return true


func cancel_collection(link: String) -> void:
	for key in get_keys("Favorites"):
		var collections: Array = get_data("Favorites", key, [])
		var changed = false
		for j in range(collections.size() - 1, -1, -1):  #倒序删除
			if collections[j]["link"] == link:
				collections.remove_at(j)
				changed = true
		if changed:
			set_data("Favorites", key, collections, true)


# 安全回调，避免对象已释放
func safe_callback(bvid: String, texture: ImageTexture, callback: Callable) -> void:
	if (
		callback == null
		or not callback.is_valid()
		or callback.get_object() == null
		or callback.get_object().is_queued_for_deletion()
		or callback.is_null()
	):
		return
	callback.call(bvid, texture)


func format_number(value: int) -> String:
	if value < 10000:
		return str(value)
	var num_in_wan = value / 10000.0
	return "%.2fw" % num_in_wan


# 格式化时间，h:mm:ss 或 m:ss
func format_time_string(input_str: String) -> String:
	var parts = input_str.split(":")
	if parts.size() != 2:
		return ""

	var total_minutes := int(parts[0])
	var seconds := int(parts[1])

	var hours := int(total_minutes / 60)
	var minutes := total_minutes % 60

	var formatted_seconds = "%02d" % seconds

	if hours > 0:
		var formatted_minutes = "%02d" % minutes
		return "%d:%s:%s" % [hours, formatted_minutes, formatted_seconds]
	else:
		return "%d:%s" % [minutes, formatted_seconds]


func _init() -> void:
	_generate_unicode_map()

	if not FileAccess.file_exists(CFG_PATH):
		FileAccess.open(CFG_PATH, FileAccess.WRITE).close()
	_config_file.load(CFG_PATH)


func _exit_tree():
	if _dirty:
		_config_file.save(CFG_PATH)
		_dirty = false


func generate_label_texture(
	background,
	text: String,
	callback: Callable,
	font: Font = null,
	font_size: int = 32,
	font_color: Color = Color.BLACK,
	viewport_size: Vector2 = Vector2.ZERO,
	bg_color: Color = Color.TRANSPARENT
) -> void:
	var viewport = SubViewport.new()
	viewport.transparent_bg = true
	viewport.render_target_clear_mode = SubViewport.CLEAR_MODE_ONCE
	add_child(viewport)

	# 处理自定义大小：若未指定，则根据背景类型自动确定
	var final_size: Vector2
	if viewport_size != Vector2.ZERO:
		final_size = viewport_size
	else:
		match typeof(background):
			TYPE_COLOR:
				final_size = Vector2(256, 128)
			TYPE_OBJECT:
				if background is Texture2D:
					final_size = background.get_size()
				else:
					viewport.queue_free()
					return
			_:
				viewport.queue_free()
				return

	viewport.size = final_size

	# 背景
	if typeof(background) == TYPE_COLOR:
		var color_rect = ColorRect.new()
		color_rect.color = background
		color_rect.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
		viewport.add_child(color_rect)

	elif background is Texture2D:
		# 使用 TextureRect 替代 Sprite2D，使其能拉伸填充整个视口
		if bg_color != Color.TRANSPARENT:
			var color_bg = ColorRect.new()
			color_bg.color = bg_color
			color_bg.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
			viewport.add_child(color_bg)

		var tex_rect = TextureRect.new()
		tex_rect.texture = background
		tex_rect.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
		tex_rect.stretch_mode = TextureRect.STRETCH_SCALE
		tex_rect.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
		viewport.add_child(tex_rect)

	else:
		viewport.queue_free()
		return

	# UI 根节点 + 文字
	var ui_root = Control.new()
	ui_root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	viewport.add_child(ui_root)

	var label = Label.new()
	label.text = text
	label.add_theme_font_size_override("font_size", font_size)
	label.add_theme_color_override("font_color", font_color)
	if font:
		label.add_theme_font_override("font", font)

	# 让 Label 填充整个区域并自动换行，确保文本完整显示
	label.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER

	ui_root.add_child(label)

	# 渲染同步
	viewport.render_target_update_mode = SubViewport.UPDATE_ONCE
	await get_tree().process_frame
	await get_tree().process_frame
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw

	# 获取图像
	var raw_texture = viewport.get_texture()
	if raw_texture == null:
		viewport.queue_free()
		return

	var img = raw_texture.get_image()
	if img == null or img.is_empty():
		viewport.queue_free()
		return

	var tex_size = img.get_size()
	if tex_size.x <= 0 or tex_size.y <= 0:
		viewport.queue_free()
		return

	viewport.queue_free()

	var final_tex = ImageTexture.create_from_image(img)
	callback.call(final_tex)


# 从链接自动识别播放类型
func detect_type(link: String) -> String:
	if link.begins_with("BV"):
		return "NetworkAudio"
	elif link.ends_with(".mp3"):
		return "MP3"
	elif link.ends_with(".m4s"):
		return "M4S"
	return "NetworkAudio"  # 默认类型


func extract_key_from_url(url: String) -> String:
	if url.is_empty():
		return ""
	var parts := url.split("/")
	if parts.is_empty():
		return ""
	return parts[-1].get_basename()


func create_frosted_texture_async(
	source: Texture2D, sigma: float = 3.0, tint: Color = Color.TRANSPARENT
) -> ImageTexture:
	var vp = SubViewport.new()
	vp.transparent_bg = true
	vp.size = source.get_size()
	vp.render_target_update_mode = SubViewport.UPDATE_ONCE
	get_tree().root.add_child(vp)

	var rect = TextureRect.new()
	rect.texture = source
	rect.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	rect.size = source.get_size()
	rect.position = Vector2.ZERO
	vp.add_child(rect)

	var mat = ShaderMaterial.new()
	mat.shader = load("res://GDShader/frosted_glass.gdshader")
	mat.set_shader_parameter("sigma", sigma)
	mat.set_shader_parameter("tint_color", tint)
	rect.material = mat

	await get_tree().process_frame

	var img = vp.get_texture().get_image()
	var result_tex = ImageTexture.create_from_image(img)

	vp.queue_free()
	return result_tex


var progress_bar_window: Window


func set_progress_bar_value(progress_value: int, title_str := "", text_str := "") -> void:
	if progress_bar_window == null:
		progress_bar_window = preload("res://Scene/ProgressBarWindow.tscn").instantiate()
		add_child(progress_bar_window)
		apply_theme_and_styles_to_node(progress_bar_window)
	progress_bar_window.update(progress_value, title_str, text_str)


var current_skin_name: String = ""


## 切换皮肤(全树应用)
func update_skin_theme(skin_name: String) -> void:
	current_skin_name = skin_name
	var dir := "res://Skin/" + skin_name + "/"

	# 1. 加载主主题
	var main_theme_path = dir + "main.theme"
	if ResourceLoader.exists(main_theme_path):
		var main_theme = ResourceLoader.load(
			main_theme_path, "Theme", ResourceLoader.CACHE_MODE_REUSE
		)
		if main_theme:
			_apply_theme_to_tree(get_tree().root, main_theme)
		else:
			push_warning("[ThemeManager] 加载 main.theme 失败: ", main_theme_path)
	else:
		push_warning("[ThemeManager] 未找到 main.theme: ", main_theme_path)

	# 2. 应用特殊样式(从配置读取)
	var config = _load_special_styles_config(dir)
	_apply_special_styles_global(config)


## 全树应用主题(不带动画)
func apply_theme(theme: Theme) -> void:
	if not theme:
		push_warning("[ThemeManager] 传入的 Theme 为 null，跳过")
		return
	_apply_theme_to_tree(get_tree().root, theme)


## 通过路径加载主题
func apply_theme_from_path(path: String) -> bool:
	if path.is_empty():
		push_error("[ThemeManager] 路径不能为空")
		return false
	if not ResourceLoader.exists(path):
		push_error("[ThemeManager] 文件不存在: ", path)
		return false
	var theme: Theme = ResourceLoader.load(path, "Theme", ResourceLoader.CACHE_MODE_REUSE)
	if not theme:
		push_error("[ThemeManager] 加载失败，文件不是有效的 Theme 资源: ", path)
		return false
	apply_theme(theme)
	return true


func update_marks_theme_and_styles() -> void:
	for i in theme_and_styles_marks:
		if is_instance_valid(i) and i.is_queued_for_deletion():
			continue
		apply_theme_and_styles_to_node(i)


## 对动态加载节点仅应用特殊样式(不改变主题)
func apply_special_style_to_node(node: Node, mark := true) -> void:
	if current_skin_name.is_empty():
		return

	var dir := "res://Skin/" + current_skin_name + "/"
	var config = _load_special_styles_config(dir)
	_apply_special_styles_recursive(node, config)


var theme_and_styles_marks: Array[Node]


## 对指定子树应用主题 特殊样式(缩小版 update)
func apply_theme_and_styles_to_node(node: Node, mark := true) -> void:
	if current_skin_name.is_empty():
		return
	var dir := "res://Skin/" + current_skin_name + "/"

	# 应用主题
	var main_theme_path = dir + "main.theme"
	if ResourceLoader.exists(main_theme_path):
		var main_theme = ResourceLoader.load(
			main_theme_path, "Theme", ResourceLoader.CACHE_MODE_REUSE
		)
		if main_theme:
			_apply_theme_to_tree(node, main_theme)
			if theme_and_styles_marks.find(node) == -1:
				theme_and_styles_marks.append(node)
		else:
			push_warning("[ThemeManager] 加载 main.theme 失败: ", main_theme_path)
	# 应用特殊样式
	var config = _load_special_styles_config(dir)
	_apply_special_styles_recursive(node, config)


## 递归应用主题(遇到 Control 设置 theme 并返回)
func _apply_theme_to_tree(node: Node, theme: Theme) -> void:
	if node is Control:
		node.theme = theme
		return
	if node is CanvasLayer:
		for child in node.get_children():
			_apply_theme_to_tree(child, theme)
		return
	for child in node.get_children():
		_apply_theme_to_tree(child, theme)


## 加载特殊样式配置(json 格式)
func _load_special_styles_config(dir: String) -> Dictionary:
	var config_path = dir + "special_styles.json"
	if not ResourceLoader.exists(config_path):
		return {}
	var file = FileAccess.open(config_path, FileAccess.READ)
	if file == null:
		push_error("[ThemeManager] 无法打开配置文件: ", config_path)
		return {}
	var content = file.get_as_text()
	file.close()
	if content.strip_edges().is_empty():
		return {}
	var json = JSON.new()
	var error = json.parse(content)
	if error != OK:
		push_error(
			"[ThemeManager] 解析 special_styles.json 失败: ", json.get_error_message(), " 内容: ", content
		)
		return {}
	var data = json.data
	if typeof(data) != TYPE_DICTIONARY:
		push_error("[ThemeManager] 配置数据不是字典类型，实际类型: ", typeof(data))
		return {}
	return data


## 全局应用特殊样式(遍历所有组)
func _apply_special_styles_global(config: Dictionary) -> void:
	if config.is_empty():
		return
	for group_name in config.keys():
		if get_tree().has_group(group_name):
			var nodes = get_tree().get_nodes_in_group(group_name)
			var value = config[group_name]
			for node in nodes:
				if node is Control:
					#printt(node.get_path(),group_name)
					_apply_single_override(node, group_name, value)


## 递归为节点及其子节点应用特殊样式(用于局部更新)
func _apply_special_styles_recursive(node: Node, config: Dictionary) -> void:
	if config.is_empty():
		return
	if node is Control:
		var node_groups = node.get_groups()
		for group_name in node_groups:
			if config.has(group_name):
				_apply_single_override(node, group_name, config[group_name])
	for child in node.get_children():
		_apply_special_styles_recursive(child, config)


## 为单个节点应用一个覆盖(根据组名解析类型和覆盖名称)
func _apply_single_override(node: Control, group_name: String, value) -> void:
	var parts = group_name.split("_", true, 1)  # 只分割第一个下划线
	if parts.size() != 2:
		push_warning("[ThemeManager] 无效的组名格式(缺少前缀): ", group_name, " 实际分割: ", parts)
		return
	var type_prefix = parts[0]
	var override_name = parts[1]
	match type_prefix:
		"style":
			var style: StyleBox = _load_resource(value)
			if style:
				node.add_theme_stylebox_override(override_name, style)
			else:
				push_warning("[ThemeManager] 加载 StyleBox 失败，值: ", value)
		"color":
			var color = Color(value)
			if color != null and color != Color.TRANSPARENT:
				node.add_theme_color_override(override_name, color)
			else:
				push_warning("[ThemeManager] 解析颜色失败，值: ", value)
		"font":
			var font: Font = _load_resource(value)
			if font:
				node.add_theme_font_override(override_name, font)
			else:
				push_warning("[ThemeManager] 加载 Font 失败，值: ", value)
		"font_size":
			var size = int(value)
			node.add_theme_font_size_override(override_name, size)
		"icon":
			var texture: Texture2D = _load_resource(value)
			if texture:
				node.add_theme_icon_override(override_name, texture)
			else:
				push_warning("[ThemeManager] 加载 Icon 失败，值: ", value)
		_:
			push_warning("[ThemeManager] 未知的类型前缀: ", type_prefix, " 在组名: ", group_name)


## 加载资源(支持路径字符串或直接资源对象)
func _load_resource(value):
	if value is Resource:
		return value
	if (
		typeof(value) == TYPE_STRING
		and (value.begins_with("res://") or value.begins_with("user://"))
	):
		if ResourceLoader.exists(value):
			var resource = ResourceLoader.load(value, "", ResourceLoader.CACHE_MODE_REUSE)
			if resource:
				return resource
			else:
				push_warning("[ThemeManager] ResourceLoader.load 返回 null: ", value)
		else:
			push_warning("[ThemeManager] 资源路径不存在: ", value)
	else:
		push_warning("[ThemeManager] _load_resource 不支持的参数类型: ", typeof(value), " 值: ", value)
	return null
