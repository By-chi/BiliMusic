extends Node
var play_ui_mode := 0:
	set(value):
		if value != play_ui_mode:
			play_ui_mode = value
			GdScriptFunc.flush()
var default_current_playlist: Array = []
