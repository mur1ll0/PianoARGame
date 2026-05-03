import argparse
from pathlib import Path
import onnx


def shape_of_value_info(value_info):
    dims = []
    tensor_type = value_info.type.tensor_type
    for dim in tensor_type.shape.dim:
        if dim.dim_value > 0:
            dims.append(int(dim.dim_value))
        elif dim.dim_param:
            dims.append(dim.dim_param)
        else:
            dims.append("?")
    return dims


def main():
    parser = argparse.ArgumentParser(description="Validate ONNX input/output contract")
    parser.add_argument("--model", required=True, help="Path to ONNX model")
    parser.add_argument("--expect-input", default="1,3,640,640", help="Expected input shape")
    parser.add_argument("--expect-output", default="1,37,8400", help="Expected output shape")
    parser.add_argument("--strict", action="store_true", help="Fail if shape differs from expected")
    args = parser.parse_args()

    model_path = Path(args.model)
    if not model_path.exists():
        raise FileNotFoundError(f"ONNX model not found: {model_path}")

    expected_input = [int(v) for v in args.expect_input.split(",")]
    expected_output = [int(v) for v in args.expect_output.split(",")]

    model = onnx.load(str(model_path))
    graph = model.graph

    if not graph.input:
        raise RuntimeError("ONNX graph has no inputs")
    if not graph.output:
        raise RuntimeError("ONNX graph has no outputs")

    input_info = graph.input[0]
    output_info = graph.output[0]

    input_shape = shape_of_value_info(input_info)
    output_shape = shape_of_value_info(output_info)

    print(f"Input name: {input_info.name}")
    print(f"Input shape: {input_shape}")
    print(f"Output name: {output_info.name}")
    print(f"Output shape: {output_shape}")

    if args.strict:
        if input_shape != expected_input:
            raise RuntimeError(f"Input shape mismatch. Expected {expected_input}, got {input_shape}")
        if output_shape != expected_output:
            raise RuntimeError(f"Output shape mismatch. Expected {expected_output}, got {output_shape}")
        print("Strict contract validation passed.")


if __name__ == "__main__":
    main()
