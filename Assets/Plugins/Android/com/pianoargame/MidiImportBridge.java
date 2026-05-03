package com.pianoargame;

import android.app.Activity;
import android.app.Fragment;
import android.app.FragmentManager;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;

import com.unity3d.player.UnityPlayer;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.List;

public final class MidiImportBridge {
    private static final String RESULT_OK_PREFIX = "OK|";
    private static final String RESULT_ERROR_PREFIX = "ERROR|";

    private MidiImportBridge() {
    }

    public static void pickAndCopyMidi(Activity activity, String destinationDir, String unityReceiver, String unityCallback) {
        if (activity == null || isEmpty(unityReceiver) || isEmpty(unityCallback)) {
            sendResult(unityReceiver, unityCallback, RESULT_ERROR_PREFIX + "Parametros invalidos para importacao MIDI.");
            return;
        }

        activity.runOnUiThread(() -> {
            try {
                FragmentManager manager = activity.getFragmentManager();
                String tag = "MidiImportBridgeFragment";
                Fragment existing = manager.findFragmentByTag(tag);
                if (existing != null) {
                    sendResult(unityReceiver, unityCallback, RESULT_ERROR_PREFIX + "Importacao ja em andamento.");
                    return;
                }

                MidiImportFragment fragment = MidiImportFragment.newInstance(destinationDir, unityReceiver, unityCallback);
                manager.beginTransaction().add(fragment, tag).commitAllowingStateLoss();
            } catch (Exception ex) {
                sendResult(unityReceiver, unityCallback, RESULT_ERROR_PREFIX + safeMessage(ex));
            }
        });
    }

    public static class MidiImportFragment extends Fragment {
        private static final int REQUEST_CODE_PICK_MIDI = 8841;

        private String destinationDir;
        private String unityReceiver;
        private String unityCallback;

        static MidiImportFragment newInstance(String destinationDir, String unityReceiver, String unityCallback) {
            MidiImportFragment fragment = new MidiImportFragment();
            Bundle args = new Bundle();
            args.putString("destinationDir", destinationDir);
            args.putString("unityReceiver", unityReceiver);
            args.putString("unityCallback", unityCallback);
            fragment.setArguments(args);
            return fragment;
        }

        @Override
        public void onCreate(Bundle savedInstanceState) {
            super.onCreate(savedInstanceState);
            setRetainInstance(true);

            Bundle args = getArguments();
            destinationDir = args != null ? args.getString("destinationDir") : null;
            unityReceiver = args != null ? args.getString("unityReceiver") : null;
            unityCallback = args != null ? args.getString("unityCallback") : null;

            if (isEmpty(unityReceiver) || isEmpty(unityCallback)) {
                closeWithResult(RESULT_ERROR_PREFIX + "Callback Unity invalido.");
                return;
            }

            launchPicker();
        }

        @Override
        public void onActivityResult(int requestCode, int resultCode, Intent data) {
            super.onActivityResult(requestCode, resultCode, data);

            if (requestCode != REQUEST_CODE_PICK_MIDI) {
                closeWithResult(RESULT_ERROR_PREFIX + "Codigo de requisicao invalido.");
                return;
            }

            if (resultCode != Activity.RESULT_OK || data == null) {
                closeWithResult("CANCEL");
                return;
            }

            try {
                List<Uri> uris = collectUris(data);
                if (uris.isEmpty()) {
                    closeWithResult("CANCEL");
                    return;
                }

                List<String> copiedPaths = new ArrayList<>();
                for (int i = 0; i < uris.size(); i++) {
                    copiedPaths.add(copyToAppStorage(uris.get(i)));
                }

                closeWithResult(RESULT_OK_PREFIX + copiedPaths.size() + "|" + joinPaths(copiedPaths));
            } catch (Exception ex) {
                closeWithResult(RESULT_ERROR_PREFIX + safeMessage(ex));
            }
        }

        private void launchPicker() {
            Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
            intent.addCategory(Intent.CATEGORY_OPENABLE);
            intent.setType("*/*");
            intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
            intent.putExtra(Intent.EXTRA_MIME_TYPES, new String[] {
                "audio/midi",
                "audio/x-midi",
                "audio/sp-midi",
                "application/octet-stream"
            });

            try {
                startActivityForResult(intent, REQUEST_CODE_PICK_MIDI);
            } catch (Exception ex) {
                closeWithResult(RESULT_ERROR_PREFIX + safeMessage(ex));
            }
        }

        private List<Uri> collectUris(Intent data) {
            ArrayList<Uri> uris = new ArrayList<>();
            if (data.getClipData() != null) {
                int count = data.getClipData().getItemCount();
                for (int i = 0; i < count; i++) {
                    Uri uri = data.getClipData().getItemAt(i).getUri();
                    if (uri != null) {
                        uris.add(uri);
                    }
                }
            } else if (data.getData() != null) {
                uris.add(data.getData());
            }

            return uris;
        }

        private String joinPaths(List<String> paths) {
            if (paths == null || paths.isEmpty()) {
                return "";
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < paths.size(); i++) {
                if (i > 0) {
                    builder.append("\n");
                }

                builder.append(paths.get(i));
            }

            return builder.toString();
        }

        private String copyToAppStorage(Uri uri) throws Exception {
            Activity activity = getActivity();
            if (activity == null) {
                throw new Exception("Activity Android indisponivel.");
            }

            String targetDirectory = destinationDir;
            if (isEmpty(targetDirectory)) {
                targetDirectory = new File(activity.getFilesDir(), "MIDI").getAbsolutePath();
            }

            File directory = new File(targetDirectory);
            if (!directory.exists() && !directory.mkdirs()) {
                throw new Exception("Nao foi possivel criar a pasta MIDI do app.");
            }

            String fileName = resolveFileName(uri);
            if (isEmpty(fileName)) {
                fileName = "imported_" + System.currentTimeMillis() + ".mid";
            }

            String lower = fileName.toLowerCase();
            if (!lower.endsWith(".mid") && !lower.endsWith(".midi")) {
                fileName += ".mid";
            }

            File outFile = new File(directory, sanitize(fileName));
            InputStream input = activity.getContentResolver().openInputStream(uri);
            if (input == null) {
                throw new Exception("Nao foi possivel ler o arquivo selecionado.");
            }

            try (InputStream in = input; FileOutputStream out = new FileOutputStream(outFile, false)) {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = in.read(buffer)) > 0) {
                    out.write(buffer, 0, read);
                }
                out.flush();
            }

            return outFile.getAbsolutePath();
        }

        private String resolveFileName(Uri uri) {
            Activity activity = getActivity();
            if (activity == null) {
                return null;
            }

            Cursor cursor = null;
            try {
                cursor = activity.getContentResolver().query(uri, null, null, null, null);
                if (cursor != null && cursor.moveToFirst()) {
                    int index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                    if (index >= 0) {
                        return cursor.getString(index);
                    }
                }
            } catch (Exception ignored) {
            } finally {
                if (cursor != null) {
                    cursor.close();
                }
            }

            String path = uri.getLastPathSegment();
            if (isEmpty(path)) {
                return null;
            }

            int slash = path.lastIndexOf('/');
            return slash >= 0 ? path.substring(slash + 1) : path;
        }

        private void closeWithResult(String result) {
            sendResult(unityReceiver, unityCallback, result);
            FragmentManager manager = getFragmentManager();
            if (manager != null) {
                manager.beginTransaction().remove(this).commitAllowingStateLoss();
            }
        }
    }

    private static void sendResult(String unityReceiver, String unityCallback, String result) {
        if (isEmpty(unityReceiver) || isEmpty(unityCallback)) {
            return;
        }

        try {
            UnityPlayer.UnitySendMessage(unityReceiver, unityCallback, result == null ? "" : result);
        } catch (Exception ignored) {
        }
    }

    private static boolean isEmpty(String value) {
        return value == null || value.trim().isEmpty();
    }

    private static String sanitize(String name) {
        return name.replaceAll("[\\\\/:*?\"<>|]", "_");
    }

    private static String safeMessage(Exception ex) {
        String message = ex.getMessage();
        return isEmpty(message) ? "Erro desconhecido" : message;
    }
}
