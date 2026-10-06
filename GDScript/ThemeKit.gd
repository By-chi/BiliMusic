extends Node

static var _lrc_regex: RegEx

static func _get_lrc_regex() -> RegEx:
	if _lrc_regex == null:
		_lrc_regex = RegEx.new()
		_lrc_regex.compile("\\[(\\d{1,3}):(\\d{2})(?:\\.(\\d{2,3}))?\\]")
	return _lrc_regex

## 解析 LRC 文本，返回 {times: Array[float], texts: Array[String]}
## 已排序、已清洗（去 ♪、"音乐"、首字母大写）
static func parse_lrc(lrc_text: String) -> Dictionary:
	var times: Array[float] = []
	var texts: Array[String] = []
	var regex := _get_lrc_regex()
	for line in lrc_text.split("\n"):
		line = line.strip_edges()
		if line.is_empty():
			continue
		var matches := regex.search_all(line)
		if matches.is_empty():
			continue
		var last_end := matches[-1].get_end()
		var content := _normalize_content(line.substr(last_end).strip_edges())
		for m in matches:
			times.append(_parse_time(m))
			texts.append(content)
	var pairs := []
	for i in times.size():
		pairs.append({"t": times[i], "s": texts[i]})
	pairs.sort_custom(func(a, b): return a.t < b.t)
	times.clear()
	texts.clear()
	for p in pairs:
		times.append(p.t)
		texts.append(p.s)
	return {"times": times, "texts": texts}

## 解析 B 站 subtitle body 数组，返回同样结构
static func parse_subtitle_body(body: Array) -> Dictionary:
	var times: Array[float] = []
	var texts: Array[String] = []
	for entry in body:
		times.append(float(entry.get("from", 0.0)))
		texts.append(_normalize_content(str(entry.get("content", ""))))
	return {"times": times, "texts": texts}

## 二分查找：返回第一个 > target 的索引
static func find_index(times: Array[float], target: float) -> int:
	var lo := 0
	var hi := times.size() - 1
	while lo <= hi:
		@warning_ignore("integer_division")
		var mid := lo + (hi - lo) / 2
		if times[mid] <= target:
			lo = mid + 1
		else:
			hi = mid - 1
	return lo

static func _normalize_content(raw: String) -> String:
	var c := raw
	if c.begins_with("♪") and c.ends_with("♪"):
		c = c.substr(1, c.length() - 2)
	c = c.strip_edges()
	if c == "音乐" or c == "music":
		return ""
	return _capitalize(c)

static func _capitalize(text: String) -> String:
	if text.is_empty():
		return text
	var words := text.split(" ")
	for i in words.size():
		if words[i] == "i":
			words[i] = "I"
	var r := " ".join(words)
	return r[0].to_upper() + r.substr(1) if r.length() > 0 else r

static func _parse_time(m: RegExMatch) -> float:
	var mins := m.get_string(1).to_int()
	var secs := m.get_string(2).to_int()
	var ms_str := m.get_string(3)
	var ms := 0.0
	if not ms_str.is_empty():
		ms = ms_str.to_float() / pow(10.0, ms_str.length())
	return mins * 60.0 + secs + ms

## 主题调用：拿当前封面（自动处理本地/网络），回调 (bvid, texture)
static func fetch_cover(video_info: Dictionary, callback: Callable) -> void:
	if video_info.is_empty():
		callback.call("", null)
		return
	if video_info.get("is_network", true):
		BilibiliApi.fetch_cover(video_info["link"], callback, 1600, 1000)
	elif FileAccess.file_exists(video_info["link"].get_basename() + ".jpg"):
		var img = Image.load_from_file(video_info["link"].get_basename() + ".jpg")
		callback.call("", ImageTexture.create_from_image(img))
	else:
		GdScriptFunc.generate_label_texture(
			Color.WHITE, video_info.get("title", "本地文件"),
			callback, null, 10, Color.BLACK, Vector2i(70, 70))

static func format_time(sec: float) -> String:
	var s := int(sec)
	@warning_ignore("integer_division")
	return "%d:%02d" % [s / 60, s % 60]
