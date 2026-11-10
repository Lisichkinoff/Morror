package ru.morror.monitor;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Bundle;
import android.os.Build;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.nio.*;
import java.util.*;
import java.util.concurrent.ArrayBlockingQueue;

/** Native decoder and touch digitizer. There is no WebView or browser in this app. */
public final class MonitorActivity extends Activity implements SurfaceHolder.Callback {
    private static MonitorActivity currentActivity;
    private static int sessionPort;
    private static String sessionToken;
    private SurfaceView surface;
    private FrameLayout root;
    private TextView status;
    private volatile boolean closed, connected;
    private volatile Socket socket;
    private Thread receiver, sender, audioThread;
    private MediaCodec decoder;
    private AudioTrack audio;
    private int videoWidth, videoHeight, sampleRate, channels;
    private boolean floatAudio;
    private long sequence;
    private int decodedFrames;
    private final Map<Integer,Integer> fingers = new HashMap<>();
    private final ArrayDeque<Control> controls = new ArrayDeque<>();
    private final ArrayBlockingQueue<byte[]> audioQueue = new ArrayBlockingQueue<>(8);
    private static final class Control { final String type; final byte[] body; final int kind;Control(String t, byte[] b) { this(t,b,16); } Control(String t,byte[] b,int k){type=t;body=b;kind=k;} }

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        if(Build.VERSION.SDK_INT>=28){WindowManager.LayoutParams p=getWindow().getAttributes();p.layoutInDisplayCutoutMode=Build.VERSION.SDK_INT>=30?WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS:WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;getWindow().setAttributes(p);}
        if(getIntent().hasExtra("port")&&getIntent().hasExtra("token")){sessionPort=getIntent().getIntExtra("port",0);sessionToken=getIntent().getStringExtra("token");}
        if(currentActivity!=null&&currentActivity!=this){currentActivity.disconnect();currentActivity.finish();}currentActivity=this;
        android.util.Log.i("MorrorMonitor","Activity created "+System.identityHashCode(this)+" session="+getIntent().hasExtra("port"));
        root = new FrameLayout(this); root.setBackgroundColor(Color.BLACK);
        surface = new SurfaceView(this); surface.getHolder().addCallback(this);
        root.addView(surface, new FrameLayout.LayoutParams(-1,-1,Gravity.CENTER));
        status = new TextView(this); status.setTextColor(Color.WHITE); status.setTextSize(18); status.setGravity(Gravity.CENTER); status.setBackgroundColor(0xdd181922);
        root.addView(status,new FrameLayout.LayoutParams(-1,-1));
        LinearLayout toolbar = new LinearLayout(this); toolbar.setPadding(12,4,12,4);
        Button exit = new Button(this); exit.setText("Отключить"); exit.setOnClickListener(v -> {sessionPort=0;sessionToken=null;finish();}); toolbar.addView(exit);
        root.addView(toolbar,new FrameLayout.LayoutParams(-2,-2,Gravity.TOP|Gravity.END));
        Runnable hideToolbar=()->toolbar.setVisibility(View.GONE);toolbar.postDelayed(hideToolbar,3000);
        root.setOnTouchListener((v,event)->{toolbar.setVisibility(View.VISIBLE);toolbar.removeCallbacks(hideToolbar);toolbar.postDelayed(hideToolbar,3000);return true;});
        surface.setOnTouchListener((v,e)->touch(e));
        root.addOnLayoutChangeListener((v,a,b,c,d,e,f,g,h)->resize());
        setContentView(root); immersive();
        show("Morror · дополнительный монитор\nПодключите телефон в Morror на ПК по USB или QR-коду и включите второй экран.");
    }
    private void immersive() { getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN|View.SYSTEM_UI_FLAG_HIDE_NAVIGATION|View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY|View.SYSTEM_UI_FLAG_LAYOUT_STABLE); }
    @Override public void onWindowFocusChanged(boolean focus) { super.onWindowFocusChanged(focus); if(focus) immersive(); }
    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent);if(!intent.hasExtra("port")||!intent.hasExtra("token"))return;setIntent(intent);sessionPort=intent.getIntExtra("port",0);sessionToken=intent.getStringExtra("token"); disconnect(); closed=false; start(); }
    @Override public void surfaceCreated(SurfaceHolder holder) { android.util.Log.i("MorrorMonitor","Surface ready "+System.identityHashCode(this));start(); }
    @Override public void surfaceChanged(SurfaceHolder holder,int format,int width,int height) { android.util.Log.i("MorrorMonitor","Surface size "+width+"x"+height); }
    @Override public void surfaceDestroyed(SurfaceHolder holder) { android.util.Log.i("MorrorMonitor","Surface closed "+System.identityHashCode(this));disconnect(); }
    @Override protected void onDestroy() { disconnect();if(currentActivity==this)currentActivity=null;super.onDestroy(); }
    private void show(String text) { runOnUiThread(()->{status.setText(text); status.setVisibility(View.VISIBLE);}); }
    private void resize() {
        if(videoWidth<=0 || videoHeight<=0 || root.getWidth()<=0 || root.getHeight()<=0) return;
        double scale=Math.min((double)root.getWidth()/videoWidth,(double)root.getHeight()/videoHeight);
        FrameLayout.LayoutParams p=new FrameLayout.LayoutParams((int)(videoWidth*scale),(int)(videoHeight*scale),Gravity.CENTER);
        if(root.getWidth()-p.width<=2&&root.getHeight()-p.height<=2){p.width=root.getWidth();p.height=root.getHeight();}
        if(surface.getLayoutParams().width!=p.width || surface.getLayoutParams().height!=p.height) surface.setLayoutParams(p);
    }
    private synchronized void start() {
        if(receiver!=null && receiver.isAlive()) {if(!connected)surface.postDelayed(()->start(),100);return;}
        int port=sessionPort; String token=sessionToken;
        if(port<1 || port>65535 || token==null || !token.matches("[a-f0-9]{64}")) return;
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        closed=false; sequence=0;decodedFrames=0; fingers.clear(); synchronized(controls){controls.clear();}
        receiver=new Thread(()->receive(port,token),"Morror video"); receiver.start();
    }
    private void receive(int port,String token) {
        try {
            show("Подключаем дополнительный монитор…");
            Socket connection=new Socket(); socket=connection; connection.setTcpNoDelay(true); connection.connect(new InetSocketAddress("127.0.0.1",port),6000); connection.setSoTimeout(15000);
            DataOutputStream out=new DataOutputStream(new BufferedOutputStream(connection.getOutputStream(),8192));
            out.write("MRRMN001".getBytes("US-ASCII")); out.write(token.getBytes("US-ASCII")); out.flush(); connected=true;android.util.Log.i("MorrorMonitor","Native transport connected");
            sender=new Thread(()->send(out),"Morror touch"); sender.start();
            DataInputStream in=new DataInputStream(new BufferedInputStream(connection.getInputStream(),65536));
            while(!closed) {
                int length=in.readInt(); if(length<9 || length>4*1024*1024) throw new IOException("Недопустимый размер пакета");
                int type=in.readUnsignedByte(); long pts=in.readLong(); byte[] data=new byte[length-9]; in.readFully(data);
                if(type==0) {
                    JSONObject hello=new JSONObject(new String(data,"UTF-8")); videoWidth=hello.getInt("width"); videoHeight=hello.getInt("height");
                    if(videoWidth<16 || videoHeight<16 || videoWidth>8192 || videoHeight>8192) throw new IOException("Недопустимое разрешение");
                    sampleRate=hello.optInt("sampleRate",48000); channels=hello.optInt("channels",2); floatAudio=hello.optBoolean("floatAudio",true);
                    runOnUiThread(()->{surface.getHolder().setFixedSize(videoWidth,videoHeight);resize();});
                } else if(type==1) {
                    if(decoder==null) configureDecoder(data);
                    if(decoder!=null) decode(data,pts);
                } else if(type==2) enqueueAudio(data);
                else if(type==3) throw new IOException(new String(data,"UTF-8"));
            }
        } catch(Exception e) { if(!closed) {android.util.Log.e("MorrorMonitor","Native receiver stopped",e);show("Передача остановлена\n"+(e.getMessage()==null?"Проверьте подключение и включите экран в Morror на ПК.":e.getMessage()));} }
        finally { connected=false; closeSocket(); releaseMedia();runOnUiThread(()->getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)); }
    }
    private static byte[] nal(byte[] data,int type) {
        for(int i=0;i+4<data.length;i++) {
            int prefix=data[i]==0 && data[i+1]==0 && data[i+2]==1?3:data[i]==0 && data[i+1]==0 && data[i+2]==0 && data[i+3]==1?4:0;
            if(prefix==0) continue; int start=i+prefix;
            if((data[start]&31)!=type) { i=start-1; continue; }
            int end=start+1;
            for(;end+3<data.length;end++) if(data[end]==0 && data[end+1]==0 && (data[end+2]==1 || data[end+2]==0 && data[end+3]==1)) break;
            if(end+3>=data.length) end=data.length;
            return Arrays.copyOfRange(data,i,end);
        }
        return null;
    }
    private void configureDecoder(byte[] data) throws Exception {
        byte[] sps=nal(data,7),pps=nal(data,8); if(sps==null || pps==null) return;
        MediaFormat f=MediaFormat.createVideoFormat("video/avc",videoWidth,videoHeight);
        f.setByteBuffer("csd-0",ByteBuffer.wrap(sps)); f.setByteBuffer("csd-1",ByteBuffer.wrap(pps)); f.setInteger("max-input-size",4*1024*1024); f.setInteger("priority",0); f.setInteger("low-latency",1);
        decoder=MediaCodec.createDecoderByType("video/avc"); decoder.configure(f,surface.getHolder().getSurface(),null,0); decoder.start();
    }
    private void decode(byte[] data,long pts) throws Exception {
        int slot=decoder.dequeueInputBuffer(20000);
        if(slot>=0) { ByteBuffer b=decoder.getInputBuffer(slot); if(b==null || b.capacity()<data.length) throw new IOException("Недостаточный буфер декодера"); b.clear();b.put(data);decoder.queueInputBuffer(slot,0,data.length,pts,0); }
        else throw new IOException("Декодер не успевает. Уменьшите разрешение или FPS на ПК.");
        MediaCodec.BufferInfo info=new MediaCodec.BufferInfo(); int output=decoder.dequeueOutputBuffer(info,10000);
        while(output>=0) {
            int next=decoder.dequeueOutputBuffer(info,0);decoder.releaseOutputBuffer(output,next<0);output=next;decodedFrames++;
            if(decodedFrames%30==0) synchronized(controls){if(controls.size()<64){controls.addLast(new Control("stats",new JSONObject().put("frames",decodedFrames).toString().getBytes("UTF-8"),18));controls.notifyAll();}}
            runOnUiThread(()->status.setVisibility(View.GONE));
        }
    }
    private void enqueueAudio(byte[] data) {
        if(sampleRate<8000 || sampleRate>192000 || channels<1 || channels>2) return;
        if(audioThread==null) { audioThread=new Thread(()->playAudio(),"Morror audio");audioThread.start(); }
        if(!audioQueue.offer(data)) {audioQueue.poll();audioQueue.offer(data);}
    }
    private void playAudio() {
        try {
            int mask=channels==1?AudioFormat.CHANNEL_OUT_MONO:AudioFormat.CHANNEL_OUT_STEREO,enc=floatAudio?AudioFormat.ENCODING_PCM_FLOAT:AudioFormat.ENCODING_PCM_16BIT;
            audio=new AudioTrack.Builder().setAudioAttributes(new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MOVIE).build()).setAudioFormat(new AudioFormat.Builder().setSampleRate(sampleRate).setChannelMask(mask).setEncoding(enc).build()).setTransferMode(AudioTrack.MODE_STREAM).setBufferSizeInBytes(Math.max(AudioTrack.getMinBufferSize(sampleRate,mask,enc),sampleRate*channels*(floatAudio?4:2)/10)).build();audio.play();
            while(!closed && connected) { byte[] data=audioQueue.take(); if(floatAudio) { FloatBuffer b=ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer();float[] f=new float[b.remaining()];b.get(f);audio.write(f,0,f.length,AudioTrack.WRITE_BLOCKING); } else audio.write(data,0,data.length); }
        } catch(Exception ignored) { }
    }
    private boolean touch(MotionEvent event) {
        if(!connected || decoder==null) return true;
        try {
            int action=event.getActionMasked(),index=event.getActionIndex(); String type;
            if(action==MotionEvent.ACTION_DOWN || action==MotionEvent.ACTION_POINTER_DOWN) {
                if(action==MotionEvent.ACTION_DOWN) fingers.clear();
                int id=1;while(fingers.containsValue(id)) id++;if(id>10) return true;
                fingers.put(event.getPointerId(index),id);type="start";
            } else if(action==MotionEvent.ACTION_MOVE) type="move";
            else if(action==MotionEvent.ACTION_UP || action==MotionEvent.ACTION_POINTER_UP) {fingers.remove(event.getPointerId(index));type="end";}
            else if(action==MotionEvent.ACTION_CANCEL) {fingers.clear();type="cancel";}
            else return true;
            JSONArray points=new JSONArray();
            for(int i=0;i<event.getPointerCount();i++) {
                Integer id=fingers.get(event.getPointerId(i));if(id==null) continue;
                points.put(new JSONObject().put("id",id).put("x",Math.max(0,Math.min(1,event.getX(i)/surface.getWidth()))).put("y",Math.max(0,Math.min(1,event.getY(i)/surface.getHeight()))));
            }
            JSONObject packet=new JSONObject().put("type",type).put("sequence",++sequence).put("generation",0).put("points",points);
            synchronized(controls) {
                if(type.equals("move") && !controls.isEmpty() && controls.peekLast().type.equals("move")) controls.removeLast();
                if(controls.size()>=64) throw new IOException("Канал управления перегружен");
                controls.addLast(new Control(type,packet.toString().getBytes("UTF-8")));controls.notifyAll();
            }
        } catch(Exception e) { disconnect(); show("Управление прервано. Перезапустите сеанс на ПК."); }
        return true;
    }
    private void send(DataOutputStream out) {
        try { while(!closed && connected) {
            Control packet;
            synchronized(controls) { if(controls.isEmpty()) controls.wait(1000);packet=controls.pollFirst(); }
            if(packet==null) {out.writeInt(9);out.writeByte(17);out.writeLong(0);}
            else {out.writeInt(9+packet.body.length);out.writeByte(packet.kind);out.writeLong(0);out.write(packet.body);}
            out.flush();
        }} catch(Exception e) {if(!closed) closeSocket();}
    }
    private void closeSocket() {try{if(socket!=null) socket.close();}catch(IOException ignored){}socket=null;}
    private void disconnect() {closed=true;connected=false;closeSocket();if(sender!=null)sender.interrupt();if(audioThread!=null)audioThread.interrupt();}
    private void releaseMedia() {
        if(audioThread!=null)audioThread.interrupt();
        if(decoder!=null){try{decoder.stop();decoder.release();}catch(Exception ignored){}decoder=null;}
        if(audio!=null){try{audio.pause();audio.release();}catch(Exception ignored){}audio=null;}
        audioThread=null;audioQueue.clear();
    }
}
