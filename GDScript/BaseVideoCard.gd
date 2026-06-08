extends Button
class_name BaseVideoCard

@export var author_label: Control
@export var cover_node: TextureRect
@export var color_rect: ColorRect
@export var title_label: Control

var link: String
var _reset_tween: Tween
var _hover_tween: Tween
var list_name:="":
	set(value):
		list_name=value
		if title_label is LineEdit:
			title_label.editable=list_name!=""
var author: = "":
	set(value):
		author = value
		if author_label != null:
			author_label.text = value
		_after_text_changed() 

var cover: Texture2D:
	set(value):
		cover = value
		cover_node.show()
		
		if value==null||not value.get_image(): return
		cover_node.texture = value
		
		var colors: Array[Color] = CSharpFunc.ExtractThemeColors(value.get_image(), 1, true, 0.15)
		if colors.size() > 0:
			color_rect.color = colors[0].blend(Color(1, 1, 1, 0.5))

var description: = "":
	set(value):
		description = value
		tooltip_text = CSharpFunc.ExtractSongName(title)
		printt(title,tooltip_text)

var title: = "":
	set(value):
		title = value
		if title_label != null: 
			title_label.text = value
		tooltip_text = title + "\n" + description
		_after_text_changed() 
var block_play:=false

func _after_text_changed() -> void:
	pass 

func reset() -> void:
	cover_node.hide()
	modulate.a = 0
	if _reset_tween and _reset_tween.is_running(): _reset_tween.kill()
	_reset_tween = create_tween()
	_reset_tween.tween_property(self, "modulate:a", 1.0, 0.3)
func play_card()->void:
	get_node("/root/Main").play(self)
func _on_pressed() -> void:
	if !block_play:
		play_card()
