import os
import glob

root = os.path.join('tools', 'piano_vision_training', 'datasets', 'piano_dataset', 'labels')

for split in ['train', 'val', 'test']:
    path = os.path.join(root, split)
    files = sorted(glob.glob(os.path.join(path, '*.txt')))
    total = len(files)
    nonempty = [f for f in files if os.path.getsize(f) > 0]
    print(f'{split}: total={total}, non-empty={len(nonempty)}')
    if nonempty:
        sample = nonempty[0]
        print(' sample file:', sample)
        with open(sample, 'r', encoding='utf-8') as fh:
            print(' contents:')
            for i, line in enumerate(fh):
                print(' ', line.strip())
                if i >= 4:
                    break
