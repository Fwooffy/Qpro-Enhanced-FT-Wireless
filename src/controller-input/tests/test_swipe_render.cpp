#include "../settings.hpp"
#include "../dashboard_scroll.hpp"
#include "baseline_controller_logic.hpp"
#include <fstream>
#include <iomanip>
#include <iostream>
#include <stdexcept>
#include <vector>

using namespace qpro_controller;
namespace old = qpro_controller_baseline;
int checks = 0;
void expect(bool value, const char *name) { ++checks; if (!value) throw std::runtime_error(name); }
bool equal(const Output &a, const old::Output &b) {
    return a.x==b.x && a.y==b.y && a.force==b.force && a.touch==b.touch && a.click==b.click;
}
bool neutral(const Output &value) { return !value.x && !value.y && !value.force && !value.touch && !value.click; }
void parity() {
    for (int mode : {0,1}) for (int render : {90,120,144}) for (int source : {40,60,90}) {
        Settings settings; settings.mode = static_cast<Mode>(mode);
        old::Settings previous; previous.mode = static_cast<old::Mode>(mode);
        ContactFilter current; old::ContactFilter baseline;
        for (int frame=0; frame<render*3; ++frame) {
            int index=frame*source/render;
            QptpSide sample{uint32_t(QPTP_VALID | (index%29<25 ? QPTP_CONTACT : 0)),
                std::sin(index*.09f)*.8f, std::cos(index*.12f)*.7f, index%17<8?.8f:.4f,44};
            uint64_t now=1000000000ULL+uint64_t(frame)*1000000000ULL/render;
            uint64_t clock=5000000000ULL+uint64_t(index)*1000000000ULL/source;
            expect(equal(current.step_packet(sample,now,settings,clock,index+1,1),
                baseline.step_packet(sample,now,previous,clock,index+1,1)),
                "Trackpad and Joystick remain bit-identical; Mouse has separate recurrence checks");
        }
    }
}
void safety() {
    constexpr uint64_t now=1000000000ULL, clock=5000000000ULL;
    Settings settings; settings.mode=Mode::Swipe; settings.smoothing_ms=0; settings.swipe_gain=.06f;
    QptpSide sample{3,0,0,.8f,44}; ContactFilter filter;
    auto initial=filter.step_packet(sample,now,settings,clock,1,1);
    expect(initial.touch&&initial.click&&!initial.x&&!initial.y,"contact anchors velocity and keeps immediate pressure click");
    sample.x=.2f; sample.y=-.1f;
    auto moved=filter.step_packet(sample,now+20000000,settings,clock+20000000,2,1);
    float alpha=-std::expm1(-20.f/12.f);
    float allowance_gain=1.f-.003f/std::hypot(.2f,.1f);
    float target_x=.6f*allowance_gain,target_y=-.3f*allowance_gain;
    expect(std::fabs(moved.x-target_x*alpha)<1e-6f&&std::fabs(moved.y-target_y*alpha)<1e-6f,
        "first velocity step matches causal 12 ms elapsed-time recurrence without changing gain");
    auto repeat=filter.step_packet(sample,now+20000000,settings,clock+20000000,2,1);
    expect(repeat.x==moved.x&&repeat.y==moved.y,"same-time duplicate cannot advance Swipe smoothing");
    auto continued=filter.step_packet(sample,now+30000000,settings,clock+20000000,2,1);
    expect(std::fabs(continued.x-(moved.x+(-std::expm1(-10.f/12.f))*(target_x-moved.x)))<1e-6f,
        "held velocity advances on render time even without another source packet");
    sample.force=.4f;
    auto pressure=filter.step_packet(sample,now+40000000,settings,clock+40000000,3,1);
    expect(!pressure.click&&pressure.touch&&pressure.force==.4f,"pressure click releases on source sample while velocity is smoothed");
    for (int cause=0; cause<7; ++cause) {
        ContactFilter current;
        sample={3,0,0,.8f,44}; current.step_packet(sample,now,settings,clock,1,1,0);
        sample.x=.2f; sample.y=.1f; current.step_packet(sample,now+20000000,settings,clock+20000000,2,1,0);
        Settings changed=settings; QptpSide next=sample; bool available=true;
        uint64_t next_clock=clock+40000000, epoch=1;
        if(cause==0) next.flags=0;
        if(cause==1) available=false;
        if(cause==2) changed.side=ControllerSide::Right;
        if(cause==3) next_clock=clock;
        if(cause==4) next_clock=clock+220000000;
        if(cause==5) ++epoch;
        if(cause==6) next.x=std::numeric_limits<float>::quiet_NaN();
        auto out=current.step_packet(next,now+40000000,changed,next_clock,3,epoch,0,available);
        if(cause==5) expect(out.touch&&!out.x&&!out.y,"new epoch anchors instead of carrying old Swipe motion");
        else expect(neutral(out),"invalid, Stop/unavailable, excluded side and source discontinuity clear immediately");
    }
    // The candidate may shape an already permitted glide, but cannot keep a
    // released axis alive past its original cutoff or extend the gesture.
    ContactFilter current; old::ContactFilter baseline; old::Settings previous;
    previous.mode=old::Mode::Swipe; previous.smoothing_ms=0; previous.swipe_gain=.06f;
    sample={3,0,0,.8f,44}; current.step_packet(sample,now,settings,clock,1,1);
    baseline.step_packet(sample,now,previous,clock,1,1);
    sample.x=.02f;sample.y=.2f;
    current.step_packet(sample,now+20000000,settings,clock+20000000,2,1);
    baseline.step_packet(sample,now+20000000,previous,clock+20000000,2,1);
    bool separate_cutoff=false, ended=false;
    for(int frame=1;frame<150;++frame) {
        sample.flags=QPTP_VALID; uint64_t time=20000000ULL+uint64_t(frame)*10000000ULL;
        auto a=current.step_packet(sample,now+time,settings,clock+time,frame+2,1);
        auto b=baseline.step_packet(sample,now+time,previous,clock+time,frame+2,1);
        expect(a.touch==b.touch&&a.click==b.click&&a.force==b.force,"released inertia lifetime and button state match the original exactly");
        if(!b.x) expect(!a.x,"released X ends immediately at the original per-axis cutoff");
        if(!b.y) expect(!a.y,"released Y ends immediately at the original per-axis cutoff");
        separate_cutoff|=!b.x&&b.y!=0; ended|=!b.touch;
    }
    expect(separate_cutoff&&ended,"glide test covers both independent-axis cutoff and complete stop");
    current.clear(); sample={3,0,0,.8f,44};
    current.step_packet(sample,now,settings,clock,1,1); sample.x=.2f;
    current.step_packet(sample,now+20000000,settings,clock+20000000,2,1);
    sample.flags=1;current.step_packet(sample,now+30000000,settings,clock+30000000,3,1);
    sample={3,-.5f,.7f,.8f,44};
    auto contact=current.step_packet(sample,now+40000000,settings,clock+40000000,4,1);
    expect(contact.touch&&contact.click&&!contact.x&&!contact.y,"recontact during glide clears both velocity smoothing states");
    settings.mode=Mode::Mouse;
    auto mode=current.step_packet(sample,now+50000000,settings,clock+50000000,5,1);
    expect(mode.x==sample.x&&mode.y==sample.y,"mode change starts the existing Mouse anchor without a Swipe tail");
    settings.mode=Mode::Swipe;settings.glide_ms=0;
    current.step_packet(sample,now+60000000,settings,clock+60000000,6,1);sample.x=-.1f;
    current.step_packet(sample,now+80000000,settings,clock+80000000,7,1);sample.flags=1;
    expect(neutral(current.step_packet(sample,now+90000000,settings,clock+90000000,8,1)),
        "disabled glide makes lift neutral immediately instead of adding a render filter tail");
}
float quantile(std::vector<float> values,float q) {
    std::sort(values.begin(),values.end());float position=q*float(values.size()-1);size_t lo=size_t(position),hi=std::min(lo+1,values.size()-1);
    return values[lo]+(values[hi]-values[lo])*(position-float(lo));
}
struct Stats {std::vector<float> steps;double positive=0,negative=0,total_delta=0;};
void replay(const char *path,int side) {
    std::ifstream input(path,std::ios::binary);if(!input)throw std::runtime_error("missing raw capture");
    std::vector<QptpPacket> packets;std::array<uint8_t,QPTP_PACKET_BYTES> bytes;
    while(input.read(reinterpret_cast<char *>(bytes.data()),bytes.size())){QptpPacket p{};
        expect(qptp_decode(bytes.data(),bytes.size(),&p),"original trace packet is valid");packets.push_back(p);}
    expect(!input.gcount()&&!packets.empty(),"capture has complete packets");
    for(int rate:{90,120}) {
        Settings settings;settings.mode=Mode::Swipe;settings.swipe_gain=.06f;
        old::Settings previous;previous.mode=old::Mode::Swipe;previous.swipe_gain=.06f;
        ContactFilter candidate;old::ContactFilter baseline;PacketLease lease;
        size_t next=0;Stats stats[2];Output last[2]{};unsigned last_flags=0;
        uint64_t origin=packets.front().monotonic_ns,end=packets.back().monotonic_ns;
        for(uint64_t frame=0;;++frame) {
            uint64_t elapsed=frame*1000000000ULL/rate,now=1000000000ULL+elapsed;if(origin+elapsed>end)break;
            while(next<packets.size()&&packets[next].monotonic_ns<=origin+elapsed) {
                expect(lease.accept(packets[next],1000000000ULL+packets[next].monotonic_ns-origin),"recorded packets accepted without reordering");++next;}
            QptpPacket p{};uint64_t revision=0,epoch=0;bool fresh=lease.current(now,p,&revision,&epoch);
            auto a=candidate.step_packet(p.sides[side],now,settings,p.monotonic_ns,revision,epoch,side,fresh);
            auto old_value=baseline.step_packet(p.sides[side],now,previous,p.monotonic_ns,revision,epoch,side,fresh);
            Output b{old_value.x,old_value.y,old_value.force,old_value.touch,old_value.click};
            expect(a.touch==b.touch&&a.click==b.click&&a.force==b.force,"actual trace preserves original contact/glide, click and force gates");
            if(!b.touch)expect(neutral(a),"actual trace cannot bridge an invalid or ended gesture");
            if(!(p.sides[side].flags&QPTP_CONTACT)) {
                if(!b.x)expect(!a.x,"actual released X cutoff is immediate");
                if(!b.y)expect(!a.y,"actual released Y cutoff is immediate");
            }
            Output values[2]{b,a};
            for(int index=0;index<2;++index) {
                auto out=values[index];stats[index].positive+=std::max(out.y,0.f)/rate;
                stats[index].negative+=std::max(-out.y,0.f)/rate;
                if((last_flags&QPTP_CONTACT)&&(p.sides[side].flags&QPTP_CONTACT)) {
                    float delta=std::fabs(out.y-last[index].y);stats[index].steps.push_back(delta);stats[index].total_delta+=delta;}
                last[index]=out;
            }
            last_flags=p.sides[side].flags;
        }
        float before=quantile(stats[0].steps,.95f),after=quantile(stats[1].steps,.95f);
        expect(after<before&&stats[1].total_delta<stats[0].total_delta,"actual trace velocity changes are smaller at the same render rate");
        std::cout<<path<<" side "<<side<<" @"<<rate<<"Hz p95 Y step "<<before<<" -> "<<after
            <<"; mean "<<stats[0].total_delta/stats[0].steps.size()<<" -> "<<stats[1].total_delta/stats[1].steps.size()
            <<"; +/- area "<<stats[0].positive<<'/'<<stats[0].negative<<" -> "<<stats[1].positive<<'/'<<stats[1].negative<<'\n';
    }
}
int main(int argc,char **argv) {
    try{parity();safety();std::cout<<std::setprecision(9);
        if(argc!=1) {
            if(argc<3)throw std::runtime_error("supply left and right raw traces");replay(argv[1],0);replay(argv[2],1);
            for(int index=3;index<argc;++index)replay(argv[index],1);
        }
        std::cout<<"PASS "<<checks<<" private Swipe render-filter checks\n";return 0;
    }catch(const std::exception &e){std::cerr<<"FAIL after "<<checks<<": "<<e.what()<<'\n';return 1;}
}
