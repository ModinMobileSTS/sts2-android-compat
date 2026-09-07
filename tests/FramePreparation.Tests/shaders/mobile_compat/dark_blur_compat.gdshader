shader_type canvas_item;
uniform float weight = 1.0;
void fragment() {
    COLOR *= weight;
}
