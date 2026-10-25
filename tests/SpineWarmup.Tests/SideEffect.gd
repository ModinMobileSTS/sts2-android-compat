extends Node

func _enter_tree():
	Engine.set_meta("spine_test_child_entries", Engine.get_meta("spine_test_child_entries", 0) + 1)
