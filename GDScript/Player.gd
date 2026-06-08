extends AudioStreamPlayer

enum PlayMode { PLAY_ONCE, REPEAT_ONE, LIST_LOOP, SHUFFLE, COUNT }
var sonance:=false:
	set(value):
		if value!=sonance:
			sonance=value
			if sonance:play_audio.emit()
			else:stop_audio.emit()

var type: String = "":
	set(value):
		if type == value:
			return
		stream_paused=false
		type = value
		_connect_finished_signal()
		audio_type_changed.emit(type)


var playlist: Array = []
var current_index: int = 0
var current_mode: PlayMode = PlayMode.REPEAT_ONE
var _current_player_signal_connected: bool = false
var _current_video_info_cache: Dictionary = {}
var shuffle_order: Array[int] = []
var shuffle_pos: int = 0

signal mode_changed
signal song_changed(video_info:Dictionary)
signal playback_finished
signal play_audio
signal stop_audio
signal playlist_imported(playlist_name: String)
signal paused
signal resumed
signal seeked(percentage: float)
signal audio_type_changed(new_type: String)
signal song_skipped(direction: String)
signal media_started(video_info: Dictionary)

func import_playlist_by_name(list_name := "") -> void:
	if list_name == "":
		playlist = State.default_current_playlist.duplicate()
		current_index = 0 if playlist.is_empty() else 0
	else:
		playlist.clear()
		for i in GdScriptFunc.get_data("Favorites", list_name, {}):
			playlist.append(i)
		current_index = 0 if not playlist.is_empty() else 0
	playlist_imported.emit(list_name)

func get_current_video_info()->Dictionary:
	return _current_video_info_cache

func _generate_shuffle_order_exclude_first(exclude_index: int) -> Array[int]:
	var indices: Array[int] = []
	for i in range(playlist.size()):
		indices.append(i)
	if indices.size() <= 1:
		return indices
	
	for i in range(indices.size() - 1, 0, -1):
		var j = randi() % (i + 1)
		var temp = indices[i]
		indices[i] = indices[j]
		indices[j] = temp
	
	if indices[0] == exclude_index:
		var swap_pos = randi() % (indices.size() - 1) + 1
		var temp = indices[0]
		indices[0] = indices[swap_pos]
		indices[swap_pos] = temp
	return indices

func _init_shuffle_order():
	if playlist.is_empty():
		return
	var remaining: Array[int] = []
	for i in range(playlist.size()):
		if i != current_index:
			remaining.append(i)
	for i in range(remaining.size() - 1, 0, -1):
		var j = randi() % (i + 1)
		var temp = remaining[i]
		remaining[i] = remaining[j]
		remaining[j] = temp
	remaining.insert(0, current_index)
	shuffle_order = remaining
	shuffle_pos = 0

func _change_song_shuffle(is_next: bool):
	if playlist.is_empty():
		return
	if is_next:
		if shuffle_pos + 1 < shuffle_order.size():
			shuffle_pos += 1
			current_index = shuffle_order[shuffle_pos]
		else:
			var last_song = shuffle_order[shuffle_pos]
			shuffle_order = _generate_shuffle_order_exclude_first(last_song)
			shuffle_pos = 0
			current_index = shuffle_order[0]
	else:
		if shuffle_pos > 0:
			shuffle_pos -= 1
			current_index = shuffle_order[shuffle_pos]
		else:
			shuffle_pos = shuffle_order.size() - 1
			current_index = shuffle_order[shuffle_pos]
	sonance=true

func next_song():
	match current_mode:
		PlayMode.SHUFFLE:
			_change_song_shuffle(true)
		_:
			current_index = (current_index + 1) % playlist.size()
	_apply_current_song()
	song_skipped.emit("next")

func prev_song():
	match current_mode:
		PlayMode.SHUFFLE:
			_change_song_shuffle(false)
		_:
			current_index = (current_index - 1 + playlist.size()) % playlist.size()
	_apply_current_song()
	song_skipped.emit("prev")

func _apply_current_song():
	if playlist.is_empty():
		return
	current_index = clamp(current_index, 0, playlist.size() - 1)
	var video_info:Dictionary= playlist[current_index]
	play_by_video_info(video_info)
	song_changed.emit(video_info)
	_current_video_info_cache = video_info

func _on_audio_finished():
	await get_tree().process_frame #等待那边播放器调整完毕,比然现在播放可能会被播放器的暂停覆盖
	match current_mode:
		PlayMode.REPEAT_ONE:
			seek_percentage(0)
			sonance=true
		PlayMode.LIST_LOOP, PlayMode.SHUFFLE:
			next_song()
			sonance=true
		PlayMode.PLAY_ONCE:
			playback_finished.emit()
			sonance=false

func set_play_mode(mode: int):
	current_mode = mode as PlayMode
	if current_mode == PlayMode.SHUFFLE:
		_init_shuffle_order()
	mode_changed.emit()
	GdScriptFunc.set_data("PlayerData", "PlayMode", current_mode)

func _input(event: InputEvent) -> void:
	if event is InputEventKey:
		var focused_control = get_viewport().gui_get_focus_owner()
		if focused_control is LineEdit:
			return
		if Input.is_action_just_released("Pause_Play"):
			if sonance:
				pause()
			else:
				resume()
		elif Input.is_action_just_released("Forward"):
			seek_sec(get_current_time_sec()+5)
		elif Input.is_action_just_released("Rewind"):
			seek_sec(get_current_time_sec()-5)
		elif Input.is_action_just_released("Turn_Up"):
			volume_linear=minf(volume_linear+0.1,1.0)
		elif Input.is_action_just_released("Turn_Down"):
			volume_linear=maxf(volume_linear-0.1,0.0)
		elif Input.is_action_just_released("Again"):
			seek_percentage(0)
func play_by_video_info(video_info: Dictionary ={}):
	type = GdScriptFunc.detect_type(video_info["link"])
	_current_video_info_cache = video_info
	if not State.default_current_playlist.has(video_info):
		if State.default_current_playlist.size() == GdScriptFunc.get_data("PlayerData", "DefaultPlaylistMaxSize", 15):
			State.default_current_playlist.remove_at(0)
		State.default_current_playlist.append(video_info)
	var idx = playlist.find(video_info)
	if idx == -1:
		playlist.append(video_info)
		current_index = playlist.size() - 1
		if current_mode == PlayMode.SHUFFLE:
			_init_shuffle_order()
	else:
		current_index = idx
	match type:
		"NetworkAudio":
			M4SAudioPlayer.SetAudioPlayer(self)
			M4SAudioPlayer.PlayByIdentifier(video_info["link"])
		"MP3":
			stream=AudioStreamMP3.load_from_file(video_info["link"])
			play()
		"M4S":
			print(video_info["link"])
			M4SAudioPlayer.SetAudioPlayer(self)
			M4SAudioPlayer.PlayLocal(ProjectSettings.globalize_path(video_info["link"]))
	media_started.emit(video_info)
	sonance=true

func seek_percentage(value: float) -> void:
	match type:
		"NetworkAudio":
			M4SAudioPlayer.SeekPercentage(value)
		"MP3":
			seek(stream.get_length()*value)
		"M4S":
			M4SAudioPlayer.SeekPercentage(value)
	seeked.emit(value)
	sonance=true
func seek_sec(sec:float)->void:
	match type:
		"NetworkAudio":
			M4SAudioPlayer.Seek(sec)
		"MP3":
			seek(sec)
		"M4S":
			M4SAudioPlayer.Seek(sec)
	seeked.emit(get_duration()/sec)
	sonance=true
func get_current_percentage() -> float:
	match type:
		"NetworkAudio":
			return M4SAudioPlayer.GetCurrentPercentage()
		"MP3":
			return get_playback_position()/stream.get_length()
		"M4S":
			return M4SAudioPlayer.GetCurrentPercentage()
	return 0.0
func get_duration() -> float:
	match type:
		"NetworkAudio":
			return M4SAudioPlayer.GetCurrentAudioDuration()
		"MP3":
			return stream.get_length()
		"M4S":
			return M4SAudioPlayer.GetCurrentAudioDuration()
	return 0.0
func get_current_time_sec() -> float:
	match type:
		"NetworkAudio":
			return M4SAudioPlayer.GetCurrentPosition()
		"MP3":
			return get_playback_position()
		"M4S":
			return M4SAudioPlayer.GetCurrentPosition()
	return 0.0

func resume() -> void:
	match type:
		"NetworkAudio":
			M4SAudioPlayer.Resume()
		"MP3":
			stream_paused=false
		"M4S":
			M4SAudioPlayer.Resume()
	resumed.emit()
	sonance=true

func pause() -> void:
	match type:
		"NetworkAudio":
			M4SAudioPlayer.Pause()
		"MP3":
			stream_paused=true
		"M4S":
			M4SAudioPlayer.Pause()
	paused.emit()
	sonance=false

func _ready():
	_connect_finished_signal()
	var saved_mode = GdScriptFunc.get_data("PlayerData", "PlayMode", PlayMode.REPEAT_ONE)
	set_play_mode(saved_mode)

func _connect_finished_signal() -> void:
	_disconnect_finished_signal()
	match type:
		"NetworkAudio":
			if not M4SAudioPlayer.Finish.is_connected(_on_audio_finished):
				M4SAudioPlayer.Finish.connect(_on_audio_finished)
		"MP3":
			if not finished.is_connected(_on_audio_finished):
				finished.connect(_on_audio_finished)
		"M4S":
			if not M4SAudioPlayer.Finish.is_connected(_on_audio_finished):
				M4SAudioPlayer.Finish.connect(_on_audio_finished)
	_current_player_signal_connected = true

func _disconnect_finished_signal() -> void:
	if not _current_player_signal_connected:
		return
	match type:
		"NetworkAudio":
			if M4SAudioPlayer.Finish.is_connected(_on_audio_finished):
				M4SAudioPlayer.Finish.disconnect(_on_audio_finished)
		"MP3":
			if finished.is_connected(_on_audio_finished):
				finished.disconnect(_on_audio_finished)
		"M4S":
			if M4SAudioPlayer.Finish.is_connected(_on_audio_finished):
				M4SAudioPlayer.Finish.disconnect(_on_audio_finished)
	_current_player_signal_connected = false
