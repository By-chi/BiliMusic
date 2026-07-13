extends RefCounted
class_name BilibiliLyricsCache

## 歌词缓存管理器 —— 负责本地歌词文件的索引、存储与淘汰

var _index := {}           # { md5_key: { "file": filename, "time": unix_time } }
var _loaded := false


func _init() -> void:
	_load_index()


## 从持久化存储恢复索引
func _load_index() -> void:
	if _loaded:
		return
	var keys = GdScriptFunc.get_keys("LyricsCache")
	for key in keys:
		var entry = GdScriptFunc.get_data("LyricsCache", key)
		if typeof(entry) == TYPE_DICTIONARY:
			var file = entry.get("file", "")
			var time = entry.get("time", 0)
			if not file.is_empty():
				_index[key] = {"file": file, "time": time}
	_loaded = true


## 将索引写回持久化存储（全量覆写）
func _save_index() -> void:
	var old_keys = GdScriptFunc.get_keys("LyricsCache")
	for key in old_keys:
		GdScriptFunc.remove_key("LyricsCache", key)

	for key in _index:
		var entry: Dictionary = _index[key]
		GdScriptFunc.set_data("LyricsCache", key, entry)


## 根据 request_id 生成唯一缓存键
static func _make_key(request_id: String) -> String:
	return request_id.md5_text()


## 检查请求是否有缓存命中，返回绝对路径，若无返回空字符串
func get_cached_path(request_id: String) -> String:
	_load_index()
	var key := _make_key(request_id)
	if not _index.has(key):
		return ""
	var entry: Dictionary = _index[key]
	var file_path := BilibiliConstants.LYRICS_CACHE_DIR.path_join(entry["file"])
	if not FileAccess.file_exists(file_path):
		_index.erase(key)
		_save_index()
		return ""
	return file_path


## 将外部生成的歌词文件复制到缓存目录，并记录索引
## 返回缓存目录中的最终文件路径；若失败则返回空字符串
func add_to_cache(request_id: String, source_path: String) -> String:
	if source_path.is_empty() or not FileAccess.file_exists(source_path):
		push_error("歌词缓存：源文件不存在 " + source_path)
		return ""

	_load_index()
	var dir := BilibiliConstants.LYRICS_CACHE_DIR
	if not DirAccess.dir_exists_absolute(dir):
		DirAccess.make_dir_recursive_absolute(dir)

	var key := _make_key(request_id)
	var filename := key + ".lrc"
	var dest_path := dir.path_join(filename)

	# 复制文件内容
	var src := FileAccess.open(source_path, FileAccess.READ)
	if not src:
		push_error("歌词缓存：无法读取源文件 " + source_path)
		return ""
	var data := src.get_buffer(src.get_length())
	src.close()

	var dst := FileAccess.open(dest_path, FileAccess.WRITE)
	if not dst:
		push_error("歌词缓存：无法写入目标文件 " + dest_path)
		return ""
	dst.store_buffer(data)
	dst.close()

	# 更新索引
	var now := Time.get_unix_time_from_system()
	_index[key] = {"file": filename, "time": now}

	# 淘汰最旧项
	if _index.size() > BilibiliConstants.MAX_LYRICS_CACHE_SIZE:
		_evict_oldest()

	_save_index()
	return dest_path


## 淘汰时间戳最小的条目
func _evict_oldest() -> void:
	var sorted := []
	for k in _index:
		sorted.append({"key": k, "time": _index[k]["time"]})
	sorted.sort_custom(func(a, b): return a["time"] < b["time"])
	var to_remove := _index.size() - BilibiliConstants.MAX_LYRICS_CACHE_SIZE
	for i in range(to_remove):
		var item = sorted[i]
		var old_file: String = _index[item["key"]]["file"]
		var old_path := BilibiliConstants.LYRICS_CACHE_DIR.path_join(old_file)
		if FileAccess.file_exists(old_path):
			DirAccess.remove_absolute(old_path)
		_index.erase(item["key"])
