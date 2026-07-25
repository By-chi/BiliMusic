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
		print("list_name: ", list_name)
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


var description := "":
	set(value):
		description = value
		var font: Font = title_label.get_theme_font("font", "Label")
		tooltip_text = GdScriptFunc.format_for_tooltip(value, 1000, font, 30, 30, 8)
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
