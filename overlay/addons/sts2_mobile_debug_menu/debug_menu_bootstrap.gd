extends Node

const DEBUG_MENU_SCENE := "res://addons/debug_menu/debug_menu.tscn"

var _menu: CanvasLayer

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	_install_menu.call_deferred()

func _install_menu() -> void:
	if _menu != null and is_instance_valid(_menu):
		return
	var packed := load(DEBUG_MENU_SCENE)
	if packed == null:
		push_warning("STS2 debug menu scene missing: %s" % DEBUG_MENU_SCENE)
		return
	_menu = packed.instantiate()
	_menu.name = "STS2DebugMenu"
	add_child(_menu)
	if _menu.has_method("update_settings_label"):
		_menu.call_deferred("update_settings_label")
	_menu.set("style", 2)
