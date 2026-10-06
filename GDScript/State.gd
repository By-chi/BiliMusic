extends Node
var play_ui_mode := 0:
	set(value):
		if value != play_ui_mode:
			play_ui_mode = value
			GdScriptFunc.flush()
var default_current_playlist: Array = []
func _ready() -> void:
	get_window().size=GdScriptFunc.get_data("Window","Size",Vector2i(1600,1000))
	if GdScriptFunc.get_data("Options","RememberWindow",true):
		var pos:Vector2i=GdScriptFunc.get_data("Window","Position",Vector2i(-114514,-114514))
		if pos!=Vector2i(-114514,-114514):
			get_window().position=pos
