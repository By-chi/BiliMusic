extends Node
## 收藏夹索引，加速 "某首歌是否已收藏" 查询

var _index: Dictionary = {}     # link -> { dir_name: true }
var _dirty: bool = true

func invalidate() -> void:
	_dirty = true

func _ensure() -> void:
	if not _dirty:
		return
	_index.clear()
	for dir_name in GdScriptFunc.get_keys("Favorites"):
		var list: Array = GdScriptFunc.get_data("Favorites", dir_name, [])
		for item in list:
			var link: String = item.get("link", "")
			if link.is_empty():
				continue
			if not _index.has(link):
				_index[link] = {}
			_index[link][dir_name] = true
	_dirty = false

func is_collected(link: String) -> bool:
	_ensure()
	return _index.has(link)

func dirs_containing(link: String) -> Array:
	_ensure()
	return _index.get(link, {}).keys()

func contains_in_dir(link: String, dir_name: String) -> bool:
	_ensure()
	return _index.get(link, {}).has(dir_name)
