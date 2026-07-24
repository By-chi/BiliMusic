extends Button
class_name BaseVideoCard

const PUNCT_START := "，。！？；：”’）】》、．…～·"

@export var author_label: Control
@export var cover_node: TextureRect
@export var color_rect: ColorRect
@export var title_label: Control

var link: String
var _reset_tween: Tween
@warning_ignore("unused_private_class_variable")#继承于BaseVideoCard的所有子类都有调用,所以放在这里
var _hover_tween: Tween
var list_name := "":
	set(value):
		list_name = value
		if title_label is LineEdit:
			title_label.editable = list_name != ""
var author := "":
	set(value):
		author = value
		if author_label != null:
			author_label.text = value
		_after_text_changed()

var cover: Texture2D:
	set(value):
		cover = value
		cover_node.show()

		if value == null || not value.get_image():
			return
		cover_node.texture = value

		var colors: Array[Color] = CSharpFunc.ExtractThemeColors(value.get_image(), 1, true, 0.15)
		if colors.size() > 0:
			color_rect.color = colors[0].blend(Color(1, 1, 1, 0.5))


func _format_for_tooltip(raw_text: String, max_pixel_width: float, font: Font, font_size: int, font_size_fake: int, max_lines: int) -> String:
	if raw_text.is_empty():
		return ""

	var para := TextParagraph.new()
	para.clear()
	para.add_string(raw_text, font, font_size)
	para.set_width(max_pixel_width)
	# 只保留 set_width，删除 para.width = 重复代码

	# 断行标识，如果报常量不存在直接注释本行

	var flags = TextServer.BREAK_MANDATORY | TextServer.BREAK_WORD_BOUND | TextServer.BREAK_ADAPTIVE | TextServer.BREAK_TRIM_EDGE_SPACES
	para.set_break_flags(flags)

	var lines: Array[String] = []
	var total_lines = para.get_line_count()
	var loop_max = min(total_lines, max_lines)

	var i = 0
	while i < loop_max:
		var range: Vector2i = para.get_line_range(i)
		var start = range.x
		var end = range.y
		var line_str = raw_text.substr(start, end - start)
		line_str = line_str.rstrip("\n").lstrip("\n")
		lines.append(line_str)
		i += 1

	if total_lines > max_lines:
		lines[lines.size() - 1] += "…"

	# 拼接文本，去除末尾多余换行
	var res = "\n".join(lines)
	if res == "": res = "[暂无简介]"
	print("总行数：", para.get_line_count())
	return res

var description := "":
	set(value):
		description = value
		var font: Font = title_label.get_theme_font("font", "Label")
		var font_size: int = title_label.get_theme_font_size("font_size", "Label")
		tooltip_text = _format_for_tooltip(value, 1000, font, 30, 30, 8)
var title := "":
	set(value):
		title = value
		if title_label != null:
			title_label.text = value
		_after_text_changed()
var block_play := false


func _after_text_changed() -> void:
	pass


func reset() -> void:
	cover_node.hide()
	modulate.a = 0
	if _reset_tween and _reset_tween.is_running():
		_reset_tween.kill()
	_reset_tween = create_tween()
	_reset_tween.tween_property(self, "modulate:a", 1.0, 0.3)


func play_card() -> void:
	get_node("/root/Main").play(self)


func _on_pressed() -> void:
	if !block_play:
		play_card()
