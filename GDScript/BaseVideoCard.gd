extends Button
class_name BaseVideoCard

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
func _format_for_tooltip(raw_text: String, chars_per_line: int = 35, max_lines: int = 8) -> String:
	if raw_text.is_empty():
		return ""
	
	var lines: Array[String] = []
	var current_line := ""
	
	for ch in raw_text:
		current_line += ch
		if current_line.length() >= chars_per_line:
			lines.append(current_line)
			current_line = ""
			if lines.size() >= max_lines:
				break
	
	if not current_line.is_empty() and lines.size() < max_lines:
		lines.append(current_line)
	
	if lines.size() >= max_lines:
		var last_line := lines[max_lines - 1]
		if raw_text.length() > last_line.length() + (lines.size() - 1) * chars_per_line:
			lines[max_lines - 1] = last_line + "…"
	
	return "\n".join(lines)

var description := "":
	set(value):
		description = value
		tooltip_text =_format_for_tooltip(value)
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
