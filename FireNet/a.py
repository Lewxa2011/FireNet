import os
import re

def remove_csharp_comments(code):
    single_line_comment_pattern = r'//.*?$'
    multi_line_comment_pattern = r'/\*.*?\*/'

    pattern = re.compile(
        f'({single_line_comment_pattern})|({multi_line_comment_pattern})',
        re.MULTILINE | re.DOTALL
    )

    def replacer(match):
        return ''  # remove matched comment

    return pattern.sub(replacer, code)


def process_directory(root_dir, output_dir=None):
    root_dir = os.path.abspath(root_dir)
    if not os.path.isdir(root_dir):
        print(f"[ERROR] Directory not found: {root_dir}")
        return

    file_count = 0
    processed_count = 0

    for subdir, _, files in os.walk(root_dir):
        for filename in files:
            if filename.endswith('.cs'):
                file_count += 1
                full_path = os.path.join(subdir, filename)
                try:
                    with open(full_path, 'r', encoding='utf-8') as f:
                        content = f.read()
                except Exception as e:
                    print(f"[ERROR] Failed to read {full_path}: {e}")
                    continue

                cleaned = remove_csharp_comments(content)

                if cleaned != content:
                    processed_count += 1
                    if output_dir:
                        rel_path = os.path.relpath(full_path, root_dir)
                        out_path = os.path.join(output_dir, rel_path)
                        os.makedirs(os.path.dirname(out_path), exist_ok=True)
                        try:
                            with open(out_path, 'w', encoding='utf-8') as out_file:
                                out_file.write(cleaned)
                        except Exception as e:
                            print(f"[ERROR] Failed to write {out_path}: {e}")
                    else:
                        try:
                            with open(full_path, 'w', encoding='utf-8') as f:
                                f.write(cleaned)
                        except Exception as e:
                            print(f"[ERROR] Failed to write {full_path}: {e}")

                print(f"Processed: {full_path}")

    print(f"Total .cs files found: {file_count}")
    print(f"Files with comments removed: {processed_count}")


if __name__ == '__main__':
    import argparse

    parser = argparse.ArgumentParser(description='Remove all comments from C# files in a directory recursively.')
    parser.add_argument('directory', help='Root directory containing C# files')
    parser.add_argument('--output', '-o', help='Output directory (optional). If omitted, overwrites original files.')

    args = parser.parse_args()

    if args.output:
        os.makedirs(args.output, exist_ok=True)

    process_directory(args.directory, args.output)